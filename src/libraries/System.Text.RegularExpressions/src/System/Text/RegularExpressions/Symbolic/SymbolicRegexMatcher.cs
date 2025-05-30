// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Text.RegularExpressions.Symbolic
{
    /// <summary>Represents a regex matching engine that performs regex matching using symbolic derivatives.</summary>
    internal abstract class SymbolicRegexMatcher
    {
#if DEBUG
        /// <inheritdoc cref="Regex.SaveDGML(TextWriter, int)"/>
        public abstract void SaveDGML(TextWriter writer, int maxLabelLength);

        /// <inheritdoc cref="Regex.SampleMatches(int, int)"/>
        public abstract IEnumerable<string> SampleMatches(int k, int randomseed);

        /// <inheritdoc cref="Regex.Explore(bool, bool, bool, bool, bool)"/>
        public abstract void Explore(bool includeDotStarred, bool includeReverse, bool includeOriginal, bool exploreDfa, bool exploreNfa);
#endif
    }

    /// <summary>Represents a regex matching engine that performs regex matching using symbolic derivatives.</summary>
    /// <typeparam name="TSet">Character set type.</typeparam>
    internal sealed partial class SymbolicRegexMatcher<TSet> : SymbolicRegexMatcher where TSet : IComparable<TSet>, IEquatable<TSet>
    {
        /// <summary>Sentinel value used internally by the matcher to indicate no match exists.</summary>
        private const int NoMatchExists = -2;

        /// <summary>Builder used to create <see cref="SymbolicRegexNode{S}"/>s while matching.</summary>
        /// <remarks>
        /// The builder is used to build up the DFA state space lazily, which means we need to be able to
        /// produce new <see cref="SymbolicRegexNode{S}"/>s as we match.  Once in NFA mode, we also use
        /// the builder to produce new NFA states.  The builder maintains a cache of all DFA and NFA states.
        /// </remarks>
        internal readonly SymbolicRegexBuilder<TSet> _builder;

        /// <summary>Maps every character to its corresponding minterm ID.</summary>
        private readonly MintermClassifier _mintermClassifier;

        /// <summary><see cref="_pattern"/> prefixed with [0-0xFFFF]*</summary>
        /// <remarks>
        /// The matching engine first uses <see cref="_dotStarredPattern"/> to find whether there is a match
        /// and where that match might end.  Prepending the .* prefix onto the original pattern provides the DFA
        /// with the ability to continue to process input characters even if those characters aren't part of
        /// the match. If Regex.IsMatch is used, nothing further is needed beyond this prefixed pattern.  If, however,
        /// other matching operations are performed that require knowing the exact start and end of the match,
        /// the engine then needs to process the pattern in reverse to find where the match actually started;
        /// for that, it uses the <see cref="_reversePattern"/> and walks backwards through the input characters
        /// from where <see cref="_dotStarredPattern"/> left off.  At this point we know that there was a match,
        /// where it started, and where it could have ended, but that ending point could be influenced by the
        /// selection of the starting point.  So, to find the actual ending point, the original <see cref="_pattern"/>
        /// is then used from that starting point to walk forward through the input characters again to find the
        /// actual end point used for the match.
        /// </remarks>
        internal readonly SymbolicRegexNode<TSet> _dotStarredPattern;

        /// <summary>The original regex pattern.</summary>
        internal readonly SymbolicRegexNode<TSet> _pattern;

        /// <summary>The reverse of <see cref="_pattern"/>.</summary>
        /// <remarks>
        /// Determining that there is a match and where the match ends requires only <see cref="_pattern"/>.
        /// But from there determining where the match began requires reversing the pattern and running
        /// the matcher again, starting from the ending position. This <see cref="_reversePattern"/> caches
        /// that reversed pattern used for extracting match start.
        /// </remarks>
        internal readonly SymbolicRegexNode<TSet> _reversePattern;

        /// <summary>true iff timeout checking is enabled.</summary>
        private readonly bool _checkTimeout;

        /// <summary>Timeout in milliseconds. This is only used if <see cref="_checkTimeout"/> is true.</summary>
        private readonly int _timeout;

        /// <summary>Data and routines for skipping ahead to the next place a match could potentially start.</summary>
        private readonly RegexFindOptimizations? _findOpts;

        /// <summary>
        /// Dead end state to quickly return NoMatch.
        /// </summary>
        private readonly int _deadStateId;

        /// <summary>Initial state used for vectorization.</summary>
        private readonly int _initialStateId;

        /// <summary>Whether the pattern contains any anchor.</summary>
        private readonly bool _containsAnyAnchor;

        /// <summary>Whether the pattern contains the EndZ anchor, which invalidates most optimization shortcuts.</summary>
        private readonly bool _containsEndZAnchor;

        /// <summary>The initial states for the original pattern, keyed off of the previous character kind.</summary>
        /// <remarks>If the pattern doesn't contain any anchors, there will only be a single initial state.</remarks>
        private readonly MatchingState<TSet>[] _initialStates;

        /// <summary>The initial states for the dot-star pattern, keyed off of the previous character kind.</summary>
        /// <remarks>If the pattern doesn't contain any anchors, there will only be a single initial state.</remarks>
        private readonly MatchingState<TSet>[] _dotstarredInitialStates;

        /// <summary>The initial states for the reverse pattern, keyed off of the previous character kind.</summary>
        /// <remarks>If the pattern doesn't contain any anchors, there will only be a single initial state.</remarks>
        private readonly MatchingState<TSet>[] _reverseInitialStates;

        /// <summary>Details on optimized processing of the reverse of the pattern to find the beginning of a match.</summary>
        private readonly MatchReversalInfo<TSet> _optimizedReversalInfo;

        /// <summary>Partition of the input space of sets.</summary>
        private readonly TSet[] _minterms;

        /// <summary>
        /// Character kinds <see cref="CharKind"/> for all minterms in <see cref="_minterms"/> as well as two special
        /// cases: character positions outside the input bounds and an end-of-line as the last input character.
        /// </summary>
        private readonly uint[] _positionKinds;

        /// <summary>
        /// The smallest k s.t. 2^k >= minterms.Length + 1. The "delta arrays", e.g., <see cref="_dfaDelta"/> allocate 2^k
        /// consecutive slots for each state ID to represent the transitions for each minterm. The extra slot at index
        /// _minterms.Length is used to represent an \n occurring at the very end of input, for supporting the \Z anchor.
        /// </summary>
        private readonly int _mintermsLog;

        /// <summary>Number of capture groups.</summary>
        private readonly int _capsize;

        /// <summary>Gets whether the regular expression contains captures (beyond the implicit root-level capture).</summary>
        /// <remarks>This determines whether the matcher uses the special capturing NFA simulation mode.</remarks>
        internal bool HasSubcaptures => _capsize > 1;

        /// <remarks>
        /// Both solvers supported here, <see cref="UInt64Solver"/> and <see cref="BitVectorSolver"/> are thread safe.
        /// </remarks>
        private ISolver<TSet> Solver => _builder._solver;

        /// <summary>Creates a new <see cref="SymbolicRegexMatcher{TSetType}"/>.</summary>
        /// <param name="captureCount">The number of captures in the regular expression.</param>
        /// <param name="findOptimizations">The find optimizations computed from the expression.</param>
        /// <param name="bddBuilder">The <see cref="BDD"/>-based builder.</param>
        /// <param name="rootBddNode">The root <see cref="BDD"/>-based node from the pattern.</param>
        /// <param name="solver">The solver to use.</param>
        /// <param name="matchTimeout">The match timeout to use.</param>
        public static SymbolicRegexMatcher<TSet> Create(
            int captureCount, RegexFindOptimizations findOptimizations,
            SymbolicRegexBuilder<BDD> bddBuilder, SymbolicRegexNode<BDD> rootBddNode, ISolver<TSet> solver,
            TimeSpan matchTimeout)
        {
            CharSetSolver charSetSolver = (CharSetSolver)bddBuilder._solver;

            var builder = new SymbolicRegexBuilder<TSet>(solver, charSetSolver)
            {
                // The default constructor sets the following sets to empty; they're lazily-initialized when needed.
                // Only if anchors are in the regex will these be set to non-empty.
                _wordLetterForBoundariesSet = solver.ConvertFromBDD(bddBuilder._wordLetterForBoundariesSet, charSetSolver),
                _newLineSet = solver.ConvertFromBDD(bddBuilder._newLineSet, charSetSolver)
            };

            // Convert the BDD-based AST to TSet-based AST
            SymbolicRegexNode<TSet> rootNode = bddBuilder.Transform(rootBddNode, builder, (builder, bdd) => builder._solver.ConvertFromBDD(bdd, charSetSolver));
            return new SymbolicRegexMatcher<TSet>(builder, rootNode, captureCount, findOptimizations, matchTimeout);
        }

        /// <summary>Constructs matcher for given symbolic regex.</summary>
        private SymbolicRegexMatcher(SymbolicRegexBuilder<TSet> builder, SymbolicRegexNode<TSet> rootNode, int captureCount, RegexFindOptimizations findOptimizations, TimeSpan matchTimeout)
        {
            Debug.Assert(builder._solver is UInt64Solver or BitVectorSolver, $"Unsupported solver: {builder._solver}");

            _pattern = rootNode;
            _builder = builder;
            _checkTimeout = Regex.InfiniteMatchTimeout != matchTimeout;
            _timeout = (int)(matchTimeout.TotalMilliseconds + 0.5); // Round up, so it will be at least 1ms
            TSet[]? solverMinterms = builder._solver.GetMinterms();
            Debug.Assert(solverMinterms is not null);
            _minterms = solverMinterms;
            // BitOperations.Log2 gives the integer floor of the log, so the +1 below either rounds up with non-power-of-two
            // minterms or adds an extra bit with power-of-two minterms. The extra slot at index _minterms.Length is used to
            // represent an \n occurring at the very end of input, for supporting the \Z anchor.
            _mintermsLog = BitOperations.Log2((uint)_minterms.Length) + 1;
            _mintermClassifier = builder._solver is UInt64Solver bv64 ?
                bv64._classifier :
                ((BitVectorSolver)(object)builder._solver)._classifier;
            _capsize = captureCount;

            // Initialize state and nullability arrays.
            _stateArray = new MatchingState<TSet>[InitialDfaStateCapacity];
            _stateFlagsArray = new StateFlags[InitialDfaStateCapacity];
            _nullabilityArray = new byte[InitialDfaStateCapacity];
            _dfaDelta = new int[InitialDfaStateCapacity << _mintermsLog];

            // Initialize a lookup array for the character kinds of each minterm ID. This includes one "special" minterm
            // ID _minterms.Length, which is used to represent a \n at the very end of input, and another ID -1,
            // which is used to represent any position outside the bounds of the input.
            _positionKinds = new uint[_minterms.Length + 2];
            for (int mintermId = -1; mintermId < _positionKinds.Length - 1; mintermId++)
            {
                _positionKinds[mintermId + 1] = CalculateMintermIdKind(mintermId);
            }

            // Gather optimized reversal processing information.
            _optimizedReversalInfo = CreateOptimizedReversal(_pattern.Reverse(builder));

            // Store the find optimizations that can be used to jump ahead to the next possible starting location.
            // If there's a leading beginning anchor, the find optimizations are unnecessary on top of the DFA's
            // handling for beginning anchors.
            if (findOptimizations.IsUseful &&
                findOptimizations.LeadingAnchor is not RegexNodeKind.Beginning)
            {
                _findOpts = findOptimizations;
            }

            // Determine the number of initial states. If there's no anchor, only the default previous
            // character kind 0 is ever going to be used for all initial states.
            int statesCount = _pattern._info.ContainsSomeAnchor ? CharKind.CharKindCount : 1;

            // The loops below and how character kinds are calculated assume that the "general" character kind is zero
            Debug.Assert(CharKind.General == 0);

            // Assign edge case info for quick lookup
            _containsAnyAnchor = _pattern._info.ContainsSomeAnchor;
            _containsEndZAnchor = _pattern._info.ContainsEndZAnchor;

            // Create the initial states for the original pattern.
            var initialStates = new MatchingState<TSet>[statesCount];
            for (uint charKind = 0; charKind < initialStates.Length; charKind++)
            {
                initialStates[charKind] = GetOrCreateState_NoLock(_pattern, charKind);
            }
            _initialStates = initialStates;

            // Create the dot-star pattern (a concatenation of any* with the original pattern)
            // and all of its initial states.
            _dotStarredPattern = builder.CreateConcat(builder._anyStarLazy, _pattern);
            var dotstarredInitialStates = new MatchingState<TSet>[statesCount];
            for (uint charKind = 0; charKind < dotstarredInitialStates.Length; charKind++)
            {
                // Used to detect if initial state was reentered,
                // but observe that the behavior from the state may ultimately depend on the previous
                // input char e.g. possibly causing nullability of \b or \B or of a start-of-line anchor,
                // in that sense there can be several "versions" (not more than StateCount) of the initial state.
                dotstarredInitialStates[charKind] = GetOrCreateState_NoLock(_dotStarredPattern, charKind, isInitialState: true);
            }
            _dotstarredInitialStates = dotstarredInitialStates;

            // Assign dead and initial state ids
            _deadStateId = GetOrCreateState_NoLock(_builder._nothing, 0).Id;
            _initialStateId = _dotstarredInitialStates[CharKind.General].Id;

            // Create the reverse pattern (the original pattern in reverse order) and all of its
            // initial states. Also disable backtracking simulation to ensure the reverse path from
            // the final state that was found is followed. Not doing so might cause the earliest
            // starting point to not be found.
            _reversePattern = builder.CreateDisableBacktrackingSimulation(_pattern.Reverse(builder));
            var reverseInitialStates = new MatchingState<TSet>[statesCount];
            for (uint charKind = 0; charKind < reverseInitialStates.Length; charKind++)
            {
                reverseInitialStates[charKind] = GetOrCreateState_NoLock(_reversePattern, charKind);
            }
            _reverseInitialStates = reverseInitialStates;

            // Maps a minterm ID to a character kind
            uint CalculateMintermIdKind(int mintermId)
            {
                // Only patterns with anchors use anything except the general kind
                if (_pattern._info.ContainsSomeAnchor)
                {
                    // A minterm ID of -1 represents the positions before the first and after the last character
                    // in the input.
                    if (mintermId == -1)
                    {
                        return CharKind.BeginningEnd;
                    }

                    // A minterm ID of minterms.Length represents a \n at the very end of input, which is matched
                    // by the \Z anchor.
                    if ((uint)mintermId == (uint)_minterms.Length)
                    {
                        return CharKind.NewLineS;
                    }

                    TSet minterm = _minterms[mintermId];

                    // Examine the minterm to figure out its character kind
                    if (_builder._newLineSet.Equals(minterm))
                    {
                        // The minterm is a new line character
                        return CharKind.Newline;
                    }
                    else if (!Solver.IsEmpty(Solver.And(_builder._wordLetterForBoundariesSet, minterm)))
                    {
                        Debug.Assert(Solver.IsEmpty(Solver.And(Solver.Not(_builder._wordLetterForBoundariesSet), minterm)));
                        // The minterm is a subset of word letters as considered by \b and \B
                        return CharKind.WordLetter;
                    }
                }

                // All other minterms belong to the general kind
                return CharKind.General;
            }
        }

        /// <summary>
        /// Create a PerThreadData with the appropriate parts initialized for this matcher's pattern.
        /// </summary>
        internal PerThreadData CreatePerThreadData() => new PerThreadData(_capsize);

        /// <summary>Look up what is the character kind given a position ID</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint GetPositionKind(int positionId) => _positionKinds[positionId + 1];

        /// <summary>
        /// Lookup the actual minterm based on its ID. Also get its character kind, which is a general categorization of
        /// characters used for cheaply deciding the nullability of anchors.
        /// </summary>
        internal TSet GetMintermFromId(int mintermId)
        {
            TSet[] minterms = _minterms;

            if ((uint)mintermId < (uint)minterms.Length)
            {
                return minterms[mintermId];
            }

            // A minterm ID of minterms.Length represents a \n at the very end of input, which is matched by the \Z anchor.
            return _builder._newLineSet;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint GetCharKind(ReadOnlySpan<char> input, int i) =>
            !_pattern._info.ContainsSomeAnchor ?
                CharKind.General : // The previous character kind is irrelevant when anchors are not used.
                GetPositionKind(DefaultInputReader.GetPositionId(this, input, i));

        private void CheckTimeout(long timeoutOccursAt)
        {
            Debug.Assert(_checkTimeout);
            if (Environment.TickCount64 >= timeoutOccursAt)
            {
                ThrowRegexTimeout();
            }

            void ThrowRegexTimeout() => throw new RegexMatchTimeoutException(string.Empty, string.Empty, TimeSpan.FromMilliseconds(_timeout));
        }

        /// <summary>Find a match.</summary>
        /// <param name="mode">The mode of execution based on the regex operation being performed.</param>
        /// <param name="input">The input span</param>
        /// <param name="startat">The position to start search in the input span.</param>
        /// <param name="perThreadData">Per thread data reused between calls.</param>
        public SymbolicMatch FindMatch(RegexRunnerMode mode, ReadOnlySpan<char> input, int startat, PerThreadData perThreadData)
        {
            Debug.Assert(startat >= 0 && startat <= input.Length, $"{nameof(startat)} == {startat}, {nameof(input.Length)} == {input.Length}");
            Debug.Assert(perThreadData is not null);

            // If we need to perform timeout checks, store the absolute timeout value.
            long timeoutOccursAt = 0;
            if (_checkTimeout)
            {
                // Using Environment.TickCount for efficiency instead of Stopwatch -- as in the non-DFA case.
                timeoutOccursAt = Environment.TickCount64 + _timeout;
            }

            // Phase 1:
            // Determine the end point of the match.  The returned index is one-past-the-end index for the characters
            // in the match.  Note that -1 is a valid end point for an empty match at the beginning of the input.
            // It returns NoMatchExists (-2) when there is no match.
            // As an example, consider the pattern a{1,3}(b*) run against an input of aacaaaabbbc: phase 1 will find
            // the position of the last b: aacaaaabbbc.  It additionally records the position of the first a after
            // the c as the low boundary for the starting position.

            // The Z anchor and over 255 minterms are rare enough to consider them separate edge cases.
            int matchEnd;
            if (!_containsEndZAnchor && _mintermClassifier.ByteLookup is not null)
            {
                // Optimize processing for the common case of no Z anchor and <= 255 minterms. Specialize each call with different generic method arguments.
                matchEnd = (_findOpts is not null, _containsAnyAnchor) switch
                {
                    (true, true) =>   FindEndPositionOptimized<NoZAnchorFindOptimizationsInitialStateHandler, DefaultDfaNoZAnchorOptimizedNullabilityHandler>(input, startat, timeoutOccursAt, mode, perThreadData),
                    (true, false) =>  FindEndPositionOptimized<NoAnchorsFindOptimizationsInitialStateHandler, NoAnchorDfaOptimizedNullabilityHandler>(input, startat, timeoutOccursAt, mode, perThreadData),
                    (false, true) =>  FindEndPositionOptimized<NoOptimizationsInitialStateHandler, DefaultDfaNoZAnchorOptimizedNullabilityHandler>(input, startat, timeoutOccursAt, mode, perThreadData),
                    (false, false) => FindEndPositionOptimized<NoOptimizationsInitialStateHandler, NoAnchorDfaOptimizedNullabilityHandler>(input, startat, timeoutOccursAt, mode, perThreadData),
                };
            }
            else
            {
                // Fallback for Z anchor or over 255 minterms
                matchEnd = _findOpts is not null ?
                    FindEndPositionFallback<FindOptimizationsInitialStateHandler, DefaultNullabilityHandler>(input, startat, timeoutOccursAt, mode, perThreadData) :
                    FindEndPositionFallback<NoOptimizationsInitialStateHandler, DefaultNullabilityHandler>(input, startat, timeoutOccursAt, mode, perThreadData);
            }

            // If there wasn't a match, we're done.
            if (matchEnd == NoMatchExists)
            {
                return SymbolicMatch.NoMatch;
            }

            // A match exists. If we don't need further details, e.g. because IsMatch was used (and thus we don't
            // need the exact bounds of the match, captures, etc.), we're done.
            if (mode == RegexRunnerMode.ExistenceRequired)
            {
                return SymbolicMatch.MatchExists;
            }

            // Phase 2:
            // Match backwards through the input matching against the reverse of the pattern, looking for the earliest
            // start position.  That tells us the actual starting position of the match.  We can skip this phase if we
            // recorded a fixed-length marker for the portion of the pattern that matched, as we can then jump that
            // exact number of positions backwards.  Continuing the previous example, phase 2 will walk backwards from
            // that last b until it finds the 4th a: aaabbbc.
            int matchStart = 0;
            Debug.Assert(matchEnd >= startat - 1);
            switch (_optimizedReversalInfo.Kind)
            {
                case MatchReversalKind.MatchStart:
                case MatchReversalKind.PartialFixedLength:
                    int initialLastStart = -1; // invalid sentinel value
                    int i = matchEnd;
                    CurrentState reversalStartState;

                    if (_optimizedReversalInfo.Kind is MatchReversalKind.MatchStart)
                    {
                        // No fixed-length knowledge. Start at the end of the match.
                        reversalStartState = new CurrentState(_reverseInitialStates[GetCharKind(input, matchEnd)]);
                    }
                    else
                    {
                        // There's a fixed-length portion at the end of the match. Start just before it.
                        i -= _optimizedReversalInfo.FixedLength;
                        reversalStartState = new CurrentState(_optimizedReversalInfo.AdjustedStartState!);

                        // reversal may already be nullable here in the case of anchors
                        if (_containsAnyAnchor &&
                            _nullabilityArray[reversalStartState.DfaStateId] > 0 &&
                            DefaultNullabilityHandler.IsNullableAt<DfaStateHandler>(this, in reversalStartState, DefaultInputReader.GetPositionId(this, input, i)))
                        {
                            initialLastStart = i;
                        }
                    }

                    matchStart = matchEnd < startat ? startat : (_containsEndZAnchor, _containsAnyAnchor) switch
                    {
                        // Call FindStartPosition with generic method arguments based on the presence of anchors. This is purely an optimization;
                        // the (true, true) case is functionally complete whereas the (false, false) case is the most optimized.
                        (true, true) =>   FindStartPosition<DefaultInputReader, DefaultNullabilityHandler>(reversalStartState, initialLastStart, input, i, startat, perThreadData),
                        (true, false) =>  FindStartPosition<DefaultInputReader, NoAnchorsNullabilityHandler>(reversalStartState, initialLastStart, input, i, startat, perThreadData),
                        (false, true) =>  FindStartPosition<NoZAnchorOptimizedInputReader, DefaultNullabilityHandler>(reversalStartState, initialLastStart, input, i, startat, perThreadData),
                        (false, false) => FindStartPosition<NoZAnchorOptimizedInputReader, NoAnchorsNullabilityHandler>(reversalStartState, initialLastStart, input, i, startat, perThreadData),
                    };
                    break;

                case MatchReversalKind.FixedLength:
                    // The whole match is known to be of a fixed length, so we don't need to do any processing to find its beginning, just jump there.
                    matchStart = matchEnd - _optimizedReversalInfo.FixedLength;
                    break;

                default:
                    Debug.Fail($"Unexpected reversal kind: {_optimizedReversalInfo.Kind}");
                    break;
            }

            // Phase 3:
            // If there are no subcaptures (or if they're not needed), the matching process is done.  For patterns with subcaptures
            // (captures other than the top-level capture for the whole match), we need to do an additional pass to find their bounds.
            // Continuing for the previous example, phase 3 will be executed for the characters inside the match, aaabbbc,
            // and will find associate the one capture (b*) with it's match: bbb.
            if (!HasSubcaptures || mode < RegexRunnerMode.FullMatchRequired)
            {
                return new SymbolicMatch(matchStart, matchEnd - matchStart);
            }
            else
            {
                Registers endRegisters = _containsAnyAnchor ?
                    FindSubcaptures<DefaultInputReader>(input, matchStart, matchEnd, perThreadData) :
                    FindSubcaptures<NoZAnchorOptimizedInputReader>(input, matchStart, matchEnd, perThreadData);
                return new SymbolicMatch(matchStart, matchEnd - matchStart, endRegisters.CaptureStarts, endRegisters.CaptureEnds);
            }
        }

        /// <summary>
        /// Streamlined version of <see cref="FindEndPositionFallback"/> that doesn't handle /z anchors or very large sets of minterms.
        /// </summary>
        private int FindEndPositionOptimized<TInitialStateHandler, TOptimizedNullabilityHandler>(
            ReadOnlySpan<char> input, int pos, long timeoutOccursAt, RegexRunnerMode mode, PerThreadData perThreadData)
            where TInitialStateHandler : struct, IInitialStateHandler
            where TOptimizedNullabilityHandler : struct, IDfaNoZAnchorOptimizedNullabilityHandler
        {
            // Initial state candidate.
            var currentState = new CurrentState(_dotstarredInitialStates[GetCharKind(input, pos - 1)]);
            int endPos = NoMatchExists;
            int lengthMinus1 = input.Length - 1;

            while (true)
            {
                int innerLoopLength;
                bool done;
                if (currentState.NfaState is null)
                {
                    const int DfaCharsPerTimeoutCheck = 100_000;
                    innerLoopLength = _checkTimeout && lengthMinus1 - pos > DfaCharsPerTimeoutCheck ? pos + DfaCharsPerTimeoutCheck : lengthMinus1;
                    done = FindEndPositionDeltasDFAOptimized<TInitialStateHandler, TOptimizedNullabilityHandler>(
                        input, innerLoopLength, mode, timeoutOccursAt, ref pos,
                        ref currentState.DfaStateId, ref endPos);
                }
                else
                {
                    // NFA fallback check, assume \Z and full nullability for NFA since it's already extremely rare to get here and it's not worth special-casing.
                    const int NfaCharsPerTimeoutCheck = 1_000;
                    innerLoopLength = _checkTimeout && input.Length - pos > NfaCharsPerTimeoutCheck ? pos + NfaCharsPerTimeoutCheck : input.Length;
                    done = FindEndPositionDeltasNFA<DefaultNullabilityHandler>(
                        input, innerLoopLength, mode, ref pos,
                        ref currentState, ref endPos);
                }

                // If the inner loop indicates that the search finished (for example due to reaching a deadend state) or
                // there is no more input available, then the whole search is done.
                if (done || pos >= input.Length)
                {
                    break;
                }

                // The search did not finish, so we either failed to transition (which should only happen if we were in DFA mode and
                // need to switch over to NFA mode) or ran out of input in the inner loop. Check if the inner loop still had more
                // input available.
                if (pos < innerLoopLength)
                {
                    // Because there was still more input available, a failure to transition in DFA mode must be the cause
                    // of the early exit. Upgrade to NFA mode.
                    NfaMatchingState nfaState = perThreadData.NfaState;
                    nfaState.InitializeFrom(this, GetState(currentState.DfaStateId));
                    currentState = new CurrentState(nfaState);
                }

                // Check for a timeout before continuing.
                if (_checkTimeout)
                {
                    CheckTimeout(timeoutOccursAt);
                }
            }

            return endPos;
        }

        /// <summary>Performs the initial Phase 1 match to find the end position of the match, or first final state if this is an isMatch call.</summary>
        /// <param name="input">The input text.</param>
        /// <param name="pos">The starting position in <paramref name="input"/>.</param>
        /// <param name="timeoutOccursAt">The time at which timeout occurs, if timeouts are being checked.</param>
        /// <param name="mode">The mode of execution based on the regex operation being performed.</param>
        /// <param name="perThreadData">Per thread data reused between calls.</param>
        /// <returns>
        /// A one-past-the-end index into input for the preferred match, or first final state position if isMatch is true, or NoMatchExists if no match exists.
        /// </returns>
        private int FindEndPositionFallback<TInitialStateHandler, TNullabilityHandler>(ReadOnlySpan<char> input, int pos, long timeoutOccursAt, RegexRunnerMode mode, PerThreadData perThreadData)
            where TInitialStateHandler : struct, IInitialStateHandler
            where TNullabilityHandler : struct, INullabilityHandler
        {
            var currentState = new CurrentState(_dotstarredInitialStates[GetCharKind(input, pos - 1)]);

            int endPos = NoMatchExists;

            while (true)
            {
                // Now run the DFA or NFA traversal from the current point using the current state. If timeouts are being checked,
                // we need to pop out of the inner loop every now and then to do the timeout check in this outer loop. Note that
                // the timeout exists not to provide perfect guarantees around execution time but rather as a mitigation against
                // catastrophic backtracking.  Catastrophic backtracking is not an issue for the NonBacktracking engine, but we
                // still check the timeout now and again to provide some semblance of the behavior a developer experiences with
                // the backtracking engines.  We can, however, choose a large number here, since it's not actually needed for security.
                // The fallback function has lower limits due to worse performance from edge cases
                int innerLoopLength;
                bool done;
                if (currentState.NfaState is null)
                {
                    const int DfaCharsPerTimeoutCheck = 25_000;
                    innerLoopLength = _checkTimeout && input.Length - pos > DfaCharsPerTimeoutCheck ? pos + DfaCharsPerTimeoutCheck : input.Length;
                    done = FindEndPositionDeltasDFA<TInitialStateHandler, TNullabilityHandler>(
                        input, innerLoopLength, mode, ref pos, ref currentState, ref endPos);
                }
                else
                {
                    // NFA fallback check, assume \Z and full nullability for NFA since it's already extremely rare to get here.
                    const int NfaCharsPerTimeoutCheck = 1_000;
                    innerLoopLength = _checkTimeout && input.Length - pos > NfaCharsPerTimeoutCheck ? pos + NfaCharsPerTimeoutCheck : input.Length;
                    done = FindEndPositionDeltasNFA<TNullabilityHandler>(
                        input, innerLoopLength, mode, ref pos, ref currentState, ref endPos);
                }

                // If the inner loop indicates that the search finished (for example due to reaching a deadend state) or
                // there is no more input available, then the whole search is done.
                if (done || pos >= input.Length)
                {
                    break;
                }

                // The search did not finish, so we either failed to transition (which should only happen if we were in DFA mode and
                // need to switch over to NFA mode) or ran out of input in the inner loop. Check if the inner loop still had more
                // input available.
                if (pos < innerLoopLength)
                {
                    // Because there was still more input available, a failure to transition in DFA mode must be the cause
                    // of the early exit. Upgrade to NFA mode.
                    NfaMatchingState nfaState = perThreadData.NfaState;
                    nfaState.InitializeFrom(this, GetState(currentState.DfaStateId));
                    currentState = new CurrentState(nfaState);
                }

                // Check for a timeout before continuing.
                if (_checkTimeout)
                {
                    CheckTimeout(timeoutOccursAt);
                }
            }

            return endPos;
        }


        /// <summary>
        /// This version of <see cref="FindEndPositionDeltasDFA"/> uses a different set of interfaces,
        /// which don't check for many inner loop edge cases, e.g. input end or '\n'.
        /// All edge cases are handled before entering the loop.
        /// </summary>
        private bool FindEndPositionDeltasDFAOptimized<TInitialStateHandler, TOptimizedNullabilityHandler>(
            ReadOnlySpan<char> input, int lengthMinus1, RegexRunnerMode mode,
            long timeoutOccursAt, ref int posRef, ref int currentStateIdRef, ref int endPosRef)
            where TInitialStateHandler : struct, IInitialStateHandler
            where TOptimizedNullabilityHandler : struct, IDfaNoZAnchorOptimizedNullabilityHandler
        {
            int pos = posRef;

            // Initial check for input end lifted out of the subsequent hot-path loop.
            if (pos == input.Length)
            {
                if (_stateArray[currentStateIdRef]!.IsNullableFor(_positionKinds[0]))
                {
                    // The end position kind was nullable.
                    endPosRef = pos;
                }

                return true;
            }

            // To avoid frequent reads/writes to ref and out values, make and operate on local copies, which we then copy back once before returning.
            int currStateId = currentStateIdRef;
            int endPos = endPosRef;

            byte[] mintermsLookup = _mintermClassifier.ByteLookup!;
            int deadStateId = _deadStateId;
            int initialStateId = _initialStateId;

            bool result = true;
            while (currStateId != deadStateId)
            {
                if (TInitialStateHandler.IsOptimized && currStateId == initialStateId)
                {
                    TInitialStateHandler.TryFindNextStartingPosition(this, input, ref currStateId, ref pos, mintermsLookup);
                    if (pos == input.Length)
                    {
                        // Patterns such as ^$ can be nullable right away.
                        if (_stateArray[currStateId]!.IsNullableFor(_positionKinds[0]))
                        {
                            // The end position kind was nullable.
                            endPos = pos;
                        }

                        currStateId = deadStateId;
                        break;
                    }
                }

                // Get the next character.
                char c = input[pos];

                // If the state is nullable for the next character, we found a potential end state.
                if (TOptimizedNullabilityHandler.IsNullable(this, _nullabilityArray[currStateId], c, mintermsLookup))
                {
                    endPos = pos;
                    if (mode == RegexRunnerMode.ExistenceRequired)
                    {
                        // A match is known to exist.  If that's all we need to know, we're done.
                        break;
                    }
                }

                // If there is more input available try to transition with the next character.
                // Note: the order here is important so the transition itself gets taken
                if (!DfaStateHandler.TryTakeTransition(this, ref currStateId, GetMintermId(mintermsLookup, c), timeoutOccursAt) ||
                    pos >= lengthMinus1)
                {
                    if (pos + 1 < input.Length)
                    {
                        result = false;
                        break;
                    }

                    pos++;

                    // One off check for the final position. This is just to move it out of the hot loop.
                    if (_stateFlagsArray[currStateId].IsNullable() ||
                        _stateArray[currStateId]!.IsNullableFor(_positionKinds[0]))
                    {
                        // The end position (-1) was nullable.
                        endPos = pos;
                    }

                    break;
                }

                // We successfully transitioned, so update our current input index to match.
                pos++;
            }

            // Write back the local copies of the ref values.
            posRef = pos;
            endPosRef = endPos;
            currentStateIdRef = currStateId;

            return result;
        }

        /// <summary>
        /// Workhorse inner loop for <see cref="FindEndPositionFallback{TFindOptimizationsHandler,TNullabilityHandler}"/>.  Consumes the <paramref name="input"/> character by character,
        /// starting at <paramref name="posRef"/>, for each character transitioning from one state in the DFA or NFA graph to the next state,
        /// lazily building out the graph as needed.
        /// </summary>
        /// <returns>
        /// true if all input has been explored and there's no further work to be done; false if there's more input to explore and/or
        /// we need to transition from DFA mode to NFA mode.
        /// </returns>
        private bool FindEndPositionDeltasDFA<TInitialStateHandler, TNullabilityHandler>(ReadOnlySpan<char> input, int length, RegexRunnerMode mode,
            ref int posRef, ref CurrentState stateRef, ref int endPosRef)
            where TInitialStateHandler : struct, IInitialStateHandler
            where TNullabilityHandler : struct, INullabilityHandler
        {
            // To avoid frequent reads/writes to ref and out values, make and operate on local copies, which we then copy back once before returning.
            int pos = posRef;
            int endPos = endPosRef;

            CurrentState state = stateRef;
            int deadStateId = _deadStateId;
            int initialStateId = _initialStateId;

            // Loop through each character in the input, transitioning from state to state for each.
            bool result = true;
            while (true)
            {
                if (state.DfaStateId == deadStateId ||
                    (state.DfaStateId == initialStateId && !TInitialStateHandler.TryFindNextStartingPosition(this, input, ref state.DfaStateId, ref pos, null!)))
                {
                    break;
                }

                int positionId = DefaultInputReader.GetPositionId(this, input, pos);

                // If the state is nullable for the next character, meaning it accepts the empty string,
                // we found a potential end state.
                if (TNullabilityHandler.IsNullableAt<DfaStateHandler>(this, in state, positionId))
                {
                    endPos = pos;
                    if (mode == RegexRunnerMode.ExistenceRequired)
                    {
                        // A match is known to exist.  If that's all we need to know, we're done.
                        break;
                    }
                }

                // If there is more input available try to transition with the next character.
                if (pos >= length || !DfaStateHandler.TryTakeTransition(this, ref state.DfaStateId, positionId))
                {
                    result = false;
                    break;
                }

                // We successfully transitioned, so update our current input index to match.
                pos++;
            }

            // Write back the local copies of the ref values.
            posRef = pos;
            endPosRef = endPos;
            stateRef = state;

            return result;
        }

        /// <summary>
        /// Workhorse inner loop for <see cref="FindEndPositionFallback{TFindOptimizationsHandler,TNullabilityHandler}"/>.  Consumes the <paramref name="input"/> character by character,
        /// starting at <paramref name="posRef"/>, for each character transitioning from one state in the DFA or NFA graph to the next state,
        /// lazily building out the graph as needed.
        /// </summary>
        /// <returns>
        /// A positive value if iteration completed because it reached a deadend state or nullable state and the call is an isMatch.
        /// 0 if iteration completed because we reached an initial state.
        /// A negative value if iteration completed because we ran out of input or we failed to transition.
        /// </returns>
        private bool FindEndPositionDeltasNFA<TNullabilityHandler>(
                ReadOnlySpan<char> input, int length, RegexRunnerMode mode,
                ref int posRef, ref CurrentState state, ref int endPosRef)
            where TNullabilityHandler : struct, INullabilityHandler
        {
            // To avoid frequent reads/writes to ref and out values, make and operate on local copies, which we then copy back once before returning.
            int pos = posRef;
            int endPos = endPosRef;

            // Loop through each character in the input, transitioning from state to state for each.
            bool result = true;
            while (state.NfaState!.NfaStateSet.Count != 0) // Dead end here means the set is empty
            {
                int positionId = DefaultInputReader.GetPositionId(this, input, pos);

                // If the state is nullable for the next character, meaning it accepts the empty string,
                // we found a potential end state.
                if (TNullabilityHandler.IsNullableAt<NfaStateHandler>(this, in state, positionId))
                {
                    endPos = pos;
                    if (mode == RegexRunnerMode.ExistenceRequired)
                    {
                        // A match is known to exist.  If that's all we need to know, we're done.
                        break;
                    }
                }

                // If there is more input available try to transition with the next character.
                if (pos >= length || !NfaStateHandler.TryTakeTransition(this, ref state, positionId))
                {
                    break;
                }

                // We successfully transitioned, so update our current input index to match.
                pos++;
            }

            // Write back the local copies of the ref values.
            posRef = pos;
            endPosRef = endPos;

            return result;
        }

        /// <summary>
        /// Phase 2 of matching. From a found ending position, walk in reverse through the input using the reverse pattern to find the
        /// start position of match.
        /// </summary>
        /// <remarks>
        /// The start position is known to exist; this function just needs to determine exactly what it is.
        /// We need to find the earliest (lowest index) starting position that's not earlier than <paramref name="matchStartBoundary"/>.
        /// </remarks>
        /// <param name="startState">State to start reversal from</param>
        /// <param name="initialLastStart">Either valid match start location or -1</param>
        /// <param name="input">The input text.</param>
        /// <param name="i">The ending position to walk backwards from. <paramref name="i"/> points one past the last character of the match.</param>
        /// <param name="matchStartBoundary">The initial starting location discovered in phase 1, a point we must not walk earlier than.</param>
        /// <param name="perThreadData">Per thread data reused between calls.</param>
        /// <returns>The found starting position for the match.</returns>
        private int FindStartPosition<TInputReader, TNullabilityHandler>(CurrentState startState, int initialLastStart, ReadOnlySpan<char> input, int i, int matchStartBoundary, PerThreadData perThreadData)
            where TInputReader : struct, IInputReader
            where TNullabilityHandler : struct, INullabilityHandler
        {
            Debug.Assert(i >= 0, $"{nameof(i)} == {i}");
            Debug.Assert(matchStartBoundary >= 0 && matchStartBoundary <= input.Length, $"{nameof(matchStartBoundary)} == {matchStartBoundary}");
            Debug.Assert(i >= matchStartBoundary, $"Expected {i} >= {matchStartBoundary}.");
            CurrentState currentState = startState;
            int lastStart = initialLastStart;

            // Walk backwards to the furthest accepting state of the reverse pattern but no earlier than matchStartBoundary.
            while (true)
            {
                // Run the DFA or NFA traversal backwards from the current point using the current state.
                bool done = currentState.NfaState is not null ?
                    FindStartPositionDeltasNFA<TInputReader, TNullabilityHandler>(input, ref i, matchStartBoundary, ref currentState, ref lastStart) :
                    FindStartPositionDeltasDFA<TInputReader, TNullabilityHandler>(input, ref i, matchStartBoundary, ref currentState, ref lastStart);

                // If we found the starting position, we're done.
                if (done)
                {
                    break;
                }

                // We didn't find the starting position but we did exit out of the backwards traversal.  That should only happen
                // if we were unable to transition, which should only happen if we were in DFA mode and exceeded our graph size.
                // Upgrade to NFA mode and continue.
                Debug.Assert(i >= matchStartBoundary);
                NfaMatchingState nfaState = perThreadData.NfaState;
                nfaState.InitializeFrom(this, GetState(currentState.DfaStateId));
                currentState = new CurrentState(nfaState);
            }

            Debug.Assert(lastStart != -1, "We expected to find a starting position but didn't.");
            return lastStart;
        }

        /// <summary>
        /// Workhorse inner loop for <see cref="FindStartPosition"/>.  Consumes the <paramref name="input"/> character by character in reverse,
        /// starting at <paramref name="i"/>, for each character transitioning from one state in the DFA or NFA graph to the next state,
        /// lazily building out the graph as needed.
        /// </summary>
        private bool FindStartPositionDeltasDFA<TInputReader, TNullabilityHandler>(
            ReadOnlySpan<char> input, ref int i, int startThreshold, ref CurrentState stateRef, ref int lastStart)
            where TInputReader : struct, IInputReader
            where TNullabilityHandler : struct, INullabilityHandler
        {
            // To avoid frequent reads/writes to ref values, make and operate on local copies, which we then copy back once before returning.
            int pos = i;
            CurrentState state = stateRef;

            // Loop backwards through each character in the input, transitioning from state to state for each.
            bool result = true;
            while (true)
            {
                int positionId = TInputReader.GetPositionId(this, input, pos - 1);

                // If the state accepts the empty string, we found a valid starting position.  Record it and keep going,
                // since we're looking for the earliest one to occur within bounds.
                if (_nullabilityArray[state.DfaStateId] != 0 &&
                    TNullabilityHandler.IsNullableAt<DfaStateHandler>(this, in state, positionId))
                {
                    lastStart = pos;
                }

                // If we are past the start threshold or if the state is a dead end, bail; we should have already
                // found a valid starting location.
                if (pos <= startThreshold || state.DfaStateId == _deadStateId)
                {
                    Debug.Assert(lastStart != -1);
                    break;
                }

                // Try to transition with the next character, the one before the current position.
                if (!DfaStateHandler.TryTakeTransition(this, ref state.DfaStateId, positionId))
                {
                    // Return false to indicate the search didn't finish.
                    result = false;
                    break;
                }

                // Since we successfully transitioned, update our current index to match the fact that we consumed the previous character in the input.
                pos--;
            }

            // Write back the local copies of the ref values.
            stateRef = state;
            i = pos;

            return result;
        }

        private bool FindStartPositionDeltasNFA<TInputReader, TNullabilityHandler>(ReadOnlySpan<char> input, ref int i, int startThreshold, ref CurrentState state, ref int lastStart)
            where TInputReader : struct, IInputReader
            where TNullabilityHandler : struct, INullabilityHandler
        {
            // To avoid frequent reads/writes to ref values, make and operate on local copies, which we then copy back once before returning.
            int pos = i;

            // Loop backwards through each character in the input, transitioning from state to state for each.
            bool result = true;
            while (true)
            {
                int positionId = TInputReader.GetPositionId(this, input, pos - 1);

                // If the state accepts the empty string, we found a valid starting position.  Record it and keep going,
                // since we're looking for the earliest one to occur within bounds.
                if (TNullabilityHandler.IsNullableAt<NfaStateHandler>(this, in state, positionId))
                {
                    lastStart = pos;
                }

                // If we are past the start threshold or if the state is a dead end, bail; we should have already
                // found a valid starting location.
                if (pos <= startThreshold || state.DfaStateId == _deadStateId)
                {
                    Debug.Assert(lastStart != -1);
                    break;
                }

                // Try to transition with the next character, the one before the current position.
                if (!NfaStateHandler.TryTakeTransition(this, ref state, positionId))
                {
                    // Return false to indicate the search didn't finish.
                    result = false;
                    break;
                }

                // Since we successfully transitioned, update our current index to match the fact that we consumed the previous character in the input.
                pos--;
            }

            // Write back the local copies of the ref values.
            i = pos;

            return result;
        }


        /// <summary>Run the pattern on a match to record the capture starts and ends.</summary>
        /// <param name="input">input span</param>
        /// <param name="i">inclusive start position</param>
        /// <param name="iEnd">exclusive end position</param>
        /// <param name="perThreadData">Per thread data reused between calls.</param>
        /// <returns>the final register values, which indicate capture starts and ends</returns>
        private Registers FindSubcaptures<TInputReader>(ReadOnlySpan<char> input, int i, int iEnd, PerThreadData perThreadData)
            where TInputReader : struct, IInputReader
        {
            // Pick the correct start state based on previous character kind.
            MatchingState<TSet> initialState = _initialStates[GetCharKind(input, i - 1)];

            Registers initialRegisters = perThreadData.InitialRegisters;

            // Initialize registers with -1, which means "not seen yet"
            Array.Fill(initialRegisters.CaptureStarts, -1);
            Array.Fill(initialRegisters.CaptureEnds, -1);

            // Use two maps from state IDs to register values for the current and next set of states.
            // Note that these maps use insertion order, which is used to maintain priorities between states in a way
            // that matches the order the backtracking engines visit paths.
            Debug.Assert(perThreadData.Current is not null && perThreadData.Next is not null);
            SparseIntMap<Registers> current = perThreadData.Current, next = perThreadData.Next;
            current.Clear();
            next.Clear();

            ForEachNfaState(initialState.Node, initialState.PrevCharKind, (current, initialRegisters),
                static (int nfaId, (SparseIntMap<Registers> Current, Registers InitialRegisters) args) =>
                    args.Current.Add(nfaId, args.InitialRegisters.Clone()));

            while ((uint)i < (uint)iEnd)
            {
                Debug.Assert(next.Count == 0);

                // i is guaranteed to be within bounds, so the position ID is a minterm ID
                int mintermId = TInputReader.GetPositionId(this, input, i);

                foreach ((int sourceId, Registers sourceRegisters) in current.Values)
                {
                    // Get or create the transitions
                    int offset = DeltaOffset(sourceId, mintermId);
                    (int, DerivativeEffect[])[] transitions = _capturingNfaDelta[offset] ??
                        CreateNewCapturingTransition(sourceId, mintermId, offset);

                    // Take the transitions in their prioritized order
                    for (int j = 0; j < transitions.Length; ++j)
                    {
                        (int targetStateId, DerivativeEffect[] effects) = transitions[j];

                        // Try to add the state and handle the case where it didn't exist before. If the state already
                        // exists, then the transition can be safely ignored, as the existing state was generated by a
                        // higher priority transition.
                        if (next.Add(targetStateId, out int index))
                        {
                            // Avoid copying the registers on the last transition from this state, reusing the registers instead
                            Registers newRegisters = j != transitions.Length - 1 ? sourceRegisters.Clone() : sourceRegisters;
                            newRegisters.ApplyEffects(effects, i);
                            next.Update(index, targetStateId, newRegisters);

                            int coreStateId = GetCoreStateId(targetStateId);
                            StateFlags flags = _stateFlagsArray[coreStateId];
                            Debug.Assert(coreStateId != _deadStateId);

                            if (flags.IsNullable() || (flags.CanBeNullable() && GetState(coreStateId).IsNullableFor(GetCharKind(input, i + 1))))
                            {
                                // No lower priority transitions from this or other source states are taken because the
                                // backtracking engines would return the match ending here.
                                goto BreakNullable;
                            }
                        }
                    }
                }

            BreakNullable:
                // Swap the state sets and prepare for the next character
                SparseIntMap<Registers> tmp = current;
                current = next;
                next = tmp;
                next.Clear();
                i++;
            }

            Debug.Assert(current.Count > 0);
            foreach ((int endStateId, Registers endRegisters) in current.Values)
            {
                MatchingState<TSet> endState = GetState(GetCoreStateId(endStateId));
                if (endState.IsNullableFor(GetCharKind(input, iEnd)))
                {
                    // Apply effects for finishing at the stored end state
                    endState.Node.ApplyEffects((effect, args) => args.Registers.ApplyEffect(effect, args.Pos),
                        CharKind.Context(endState.PrevCharKind, GetCharKind(input, iEnd)), (Registers: endRegisters, Pos: iEnd));
                    return endRegisters;
                }
            }

            Debug.Fail("No nullable state found in the set of end states");
            return default;
        }

        /// <summary>Look up the min term ID for the character.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetMintermId(byte[] mintermLookup, char c) =>
            c < (uint)mintermLookup.Length ?
                mintermLookup[c] :
                0;

        /// <summary>Stores additional data for tracking capture start and end positions.</summary>
        /// <remarks>The NFA simulation based third phase has one of these for each current state in the current set of live states.</remarks>
        internal struct Registers(int[] captureStarts, int[] captureEnds)
        {
            public int[] CaptureStarts { get; set; } = captureStarts;
            public int[] CaptureEnds { get; set; } = captureEnds;

            /// <summary>
            /// Applies a list of effects in order to these registers at the provided input position. The order of effects
            /// should not matter though, as multiple effects to the same capture start or end do not arise.
            /// </summary>
            /// <param name="effects">list of effects to be applied</param>
            /// <param name="pos">the current input position to record</param>
            public void ApplyEffects(DerivativeEffect[] effects, int pos)
            {
                foreach (DerivativeEffect effect in effects)
                {
                    ApplyEffect(effect, pos);
                }
            }

            /// <summary>
            /// Apply a single effect to these registers at the provided input position.
            /// </summary>
            /// <param name="effect">the effect to be applied</param>
            /// <param name="pos">the current input position to record</param>
            public void ApplyEffect(DerivativeEffect effect, int pos)
            {
                switch (effect.Kind)
                {
                    case DerivativeEffectKind.CaptureStart:
                        CaptureStarts[effect.CaptureNumber] = pos;
                        break;
                    case DerivativeEffectKind.CaptureEnd:
                        CaptureEnds[effect.CaptureNumber] = pos;
                        break;
                }
            }

            /// <summary>
            /// Make a copy of this set of registers.
            /// </summary>
            /// <returns>Registers pointing to copies of this set of registers</returns>
            public Registers Clone() => new Registers((int[])CaptureStarts.Clone(), (int[])CaptureEnds.Clone());

            /// <summary>
            /// Copy register values from another set of registers, possibly allocating new arrays if they were not yet allocated.
            /// </summary>
            /// <param name="other">the registers to copy from</param>
            public void Assign(Registers other)
            {
                if (CaptureStarts is not null && CaptureEnds is not null)
                {
                    Debug.Assert(CaptureStarts.Length == other.CaptureStarts.Length);
                    Debug.Assert(CaptureEnds.Length == other.CaptureEnds.Length);

                    Array.Copy(other.CaptureStarts, CaptureStarts, CaptureStarts.Length);
                    Array.Copy(other.CaptureEnds, CaptureEnds, CaptureEnds.Length);
                }
                else
                {
                    CaptureStarts = (int[])other.CaptureStarts.Clone();
                    CaptureEnds = (int[])other.CaptureEnds.Clone();
                }
            }
        }

        /// <summary>
        /// Per thread data to be held by the regex runner and passed into every call to FindMatch. This is used to
        /// avoid repeated memory allocation.
        /// </summary>
        internal sealed class PerThreadData
        {
            public readonly NfaMatchingState NfaState;
            /// <summary>Maps used for the capturing third phase.</summary>
            public readonly SparseIntMap<Registers>? Current, Next;
            /// <summary>Registers used for the capturing third phase.</summary>
            public readonly Registers InitialRegisters;

            public PerThreadData(int capsize)
            {
                NfaState = new NfaMatchingState();

                // Only create data used for capturing mode if there are subcaptures
                if (capsize > 1)
                {
                    Current = new SparseIntMap<Registers>();
                    Next = new SparseIntMap<Registers>();
                    InitialRegisters = new Registers(new int[capsize], new int[capsize]);
                }
            }
        }

        /// <summary>Stores the state that represents a current state in NFA mode.</summary>
        /// <remarks>The entire state is composed of a list of individual states.</remarks>
        /// <remarks>New instances should only be created once per runner.</remarks>
        internal sealed class NfaMatchingState
        {
            /// <summary>Ordered set used to store the current NFA states.</summary>
            /// <remarks>The value is unused.  The type is used purely for its keys.</remarks>
            public SparseIntMap<int> NfaStateSet = new();
            /// <summary>Scratch set to swap with <see cref="NfaStateSet"/> on each transition.</summary>
            /// <remarks>
            /// On each transition, <see cref="NfaStateSetScratch"/> is cleared and filled with the next
            /// states computed from the current states in <see cref="NfaStateSet"/>, and then the sets
            /// are swapped so the scratch becomes the current and the current becomes the scratch.
            /// </remarks>
            public SparseIntMap<int> NfaStateSetScratch = new();

            /// <summary>Resets this NFA state to represent the supplied DFA state.</summary>
            /// <param name="matcher"></param>
            /// <param name="dfaMatchingState">The DFA state to use to initialize the NFA state.</param>
            public void InitializeFrom(SymbolicRegexMatcher<TSet> matcher, MatchingState<TSet> dfaMatchingState)
            {
                NfaStateSet.Clear();

                // If the DFA state is a union of multiple DFA states, loop through all of them
                // adding an NFA state for each.
                matcher.ForEachNfaState(dfaMatchingState.Node, dfaMatchingState.PrevCharKind, NfaStateSet,
                    static (int nfaId, SparseIntMap<int> nfaStateSet) => nfaStateSet.Add(nfaId, out _));
            }
        }

        /// <summary>Represents a current state in a DFA or NFA graph walk while processing a regular expression.</summary>
        /// <remarks>This is a discriminated union between a DFA state and an NFA state. One and only one will be non-null.</remarks>
        private struct CurrentState
        {
            /// <summary>Initializes the state as a DFA state.</summary>
            public CurrentState(MatchingState<TSet> dfaState)
            {
                DfaStateId = dfaState.Id;
                NfaState = null;
            }

            /// <summary>Initializes the state as an NFA state.</summary>
            public CurrentState(NfaMatchingState nfaState)
            {
                DfaStateId = -1;
                NfaState = nfaState;
            }

            /// <summary>The DFA state.</summary>
            public int DfaStateId;
            /// <summary>The NFA state.</summary>
            public NfaMatchingState? NfaState;
        }

        /// <summary>Represents a set of routines for operating over a <see cref="CurrentState"/>.</summary>
        private interface IStateHandler
        {
            public static abstract bool IsNullableFor(SymbolicRegexMatcher<TSet> matcher, in CurrentState state, uint nextCharKind);
            public static abstract StateFlags GetStateFlags(SymbolicRegexMatcher<TSet> matcher, in CurrentState state);
        }

        /// <summary>An <see cref="IStateHandler"/> for operating over <see cref="CurrentState"/> instances configured as DFA states.</summary>
        private readonly struct DfaStateHandler : IStateHandler
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool IsNullableFor(SymbolicRegexMatcher<TSet> matcher, in CurrentState state, uint nextCharKind) =>
                matcher._nullabilityArray[state.DfaStateId] > 0 &&
                ((byte)(1 << (int)nextCharKind) & matcher._nullabilityArray[state.DfaStateId]) > 0;

            /// <summary>Take the transition to the next DFA state.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool TryTakeTransition(SymbolicRegexMatcher<TSet> matcher, ref int dfaStateId, int mintermId, long timeoutOccursAt = 0)
            {
                Debug.Assert(dfaStateId > 0, $"Expected non-zero {nameof(dfaStateId)}.");

                // Use the mintermId for the character being read to look up which state to transition to.
                // If that state has already been materialized, move to it, and we're done. If that state
                // hasn't been materialized, try to create it; if we can, move to it, and we're done.
                int dfaOffset = matcher.DeltaOffset(dfaStateId, mintermId);
                int nextStateId = matcher._dfaDelta[dfaOffset];
                if (nextStateId > 0)
                {
                    // There was an existing DFA transition to some state. Move to it and
                    // return that we're still operating as a DFA and can keep going.
                    dfaStateId = nextStateId;
                    return true;
                }

                if (matcher.TryCreateNewTransition(matcher.GetState(dfaStateId), mintermId, dfaOffset, checkThreshold: true, timeoutOccursAt, out MatchingState<TSet>? nextState))
                {
                    // We were able to create a new DFA transition to some state. Move to it and
                    // return that we're still operating as a DFA and can keep going.
                    dfaStateId = nextState.Id;
                    return true;
                }

                return false;
            }

            /// <summary>
            /// Gets context independent state information:
            /// - whether this is an initial state
            /// - whether this is a dead-end state, meaning there are no transitions possible out of the state
            /// - whether this state is unconditionally nullable
            /// - whether this state may be contextually nullable
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static StateFlags GetStateFlags(SymbolicRegexMatcher<TSet> matcher, in CurrentState state) =>
                matcher._stateFlagsArray[state.DfaStateId];
        }

        /// <summary>An <see cref="IStateHandler"/> for operating over <see cref="CurrentState"/> instances configured as NFA states.</summary>
        private readonly struct NfaStateHandler : IStateHandler
        {
            /// <summary>Check if any underlying core state starts with a line anchor.</summary>
            public static bool StartsWithLineAnchor(SymbolicRegexMatcher<TSet> matcher, in CurrentState state)
            {
                foreach (ref KeyValuePair<int, int> nfaState in CollectionsMarshal.AsSpan(state.NfaState!.NfaStateSet.Values))
                {
                    if (matcher.GetState(matcher.GetCoreStateId(nfaState.Key)).StartsWithLineAnchor)
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>Check if any underlying core state is nullable in the context of the next character kind.</summary>
            public static bool IsNullableFor(SymbolicRegexMatcher<TSet> matcher, in CurrentState state, uint nextCharKind)
            {
                foreach (ref KeyValuePair<int, int> nfaState in CollectionsMarshal.AsSpan(state.NfaState!.NfaStateSet.Values))
                {
                    if (matcher.GetState(matcher.GetCoreStateId(nfaState.Key)).IsNullableFor(nextCharKind))
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>Take the transition to the next NFA state.</summary>
            public static bool TryTakeTransition(SymbolicRegexMatcher<TSet> matcher, ref CurrentState state, int mintermId)
            {
                Debug.Assert(state.DfaStateId < 0, $"Expected negative {nameof(state.DfaStateId)}.");
                Debug.Assert(state.NfaState is not null, $"Expected non-null {nameof(state.NfaState)}.");

                NfaMatchingState nfaState = state.NfaState!;

                // Grab the sets, swapping the current active states set with the scratch set.
                SparseIntMap<int> nextStates = nfaState.NfaStateSetScratch;
                SparseIntMap<int> sourceStates = nfaState.NfaStateSet;
                nfaState.NfaStateSet = nextStates;
                nfaState.NfaStateSetScratch = sourceStates;

                // Compute the set of all unique next states from the current source states and the mintermId.
                nextStates.Clear();
                if (sourceStates.Count == 1)
                {
                    // We have a single source state.  We know its next states are already deduped,
                    // so we can just add them directly to the destination states list.
                    foreach (int nextState in GetNextStates(sourceStates.Values[0].Key, mintermId, matcher))
                    {
                        nextStates.Add(nextState, out _);
                        // Nothing is required for backtracking simulation here, since there's just one state so the
                        // transition itself already handles it.
                    }
                }
                else
                {
                    // We have multiple source states, so we need to potentially dedup across each of
                    // their next states.  For each source state, get its next states, adding each into
                    // our set (which exists purely for deduping purposes), and if we successfully added
                    // to the set, then add the known-unique state to the destination list.
                    uint nextCharKind = matcher.GetPositionKind(mintermId);
                    foreach (ref KeyValuePair<int, int> sourceState in CollectionsMarshal.AsSpan(sourceStates.Values))
                    {
                        foreach (int nextState in GetNextStates(sourceState.Key, mintermId, matcher))
                        {
                            nextStates.Add(nextState, out _);
                        }

                        // To simulate backtracking, if a source state is nullable then no further transitions are taken
                        // as the backtracking engines would prefer the match ending here.
                        int coreStateId = matcher.GetCoreStateId(sourceState.Key);
                        StateFlags flags = matcher._stateFlagsArray[coreStateId];
                        if (flags.SimulatesBacktracking() &&
                            (flags.IsNullable() || (flags.CanBeNullable() && matcher.GetState(coreStateId).IsNullableFor(nextCharKind))))
                        {
                            break;
                        }
                    }
                }

                return true;

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                static int[] GetNextStates(int sourceState, int mintermId, SymbolicRegexMatcher<TSet> matcher)
                {
                    // Calculate the offset into the NFA transition table.
                    int nfaOffset = matcher.DeltaOffset(sourceState, mintermId);

                    // Get the next NFA state.
                    return matcher._nfaDelta[nfaOffset] ?? matcher.CreateNewNfaTransition(sourceState, mintermId, nfaOffset);
                }
            }

            /// <summary>
            /// Gets context independent state information:
            /// - whether this is an initial state
            /// - whether this is a dead-end state, meaning there are no transitions possible out of the state
            /// - whether this state is unconditionally nullable
            /// - whether this state may be contextually nullable
            /// </summary>
            /// <remarks>
            /// In NFA mode:
            /// - an empty set of states means that it is a dead end
            /// - no set of states qualifies as an initial state. This could be made more accurate, but with that the
            ///   matching logic would need to be updated to handle the fact that <see cref="FindOptimizationsInitialStateHandler"/>
            ///   can transition back to a DFA state.
            /// </remarks>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static StateFlags GetStateFlags(SymbolicRegexMatcher<TSet> matcher, in CurrentState state)
            {
                // Build the flags for the set of states by taking a bitwise Or of all the per-state flags and then
                // masking out the irrelevant ones. This works because IsNullable and CanBeNullable should be true if
                // they are true for any state in the set; SimulatesBacktracking is true for all the states if
                // it is true for any state (since it is a phase-wide property); and all other flags are masked out.
                StateFlags flags = 0;
                foreach (ref KeyValuePair<int, int> nfaState in CollectionsMarshal.AsSpan(state.NfaState!.NfaStateSet.Values))
                {
                    flags |= matcher._stateFlagsArray[matcher.GetCoreStateId(nfaState.Key)];
                }

                return flags & (StateFlags.IsNullableFlag | StateFlags.CanBeNullableFlag | StateFlags.SimulatesBacktrackingFlag);
            }

#if DEBUG
            /// <summary>Undo a previous call to <see cref="TryTakeTransition"/>.</summary>
            public static void UndoTransition(ref CurrentState state)
            {
                Debug.Assert(state.DfaStateId < 0, $"Expected negative {nameof(state.DfaStateId)}.");
                Debug.Assert(state.NfaState is not null, $"Expected non-null {nameof(state.NfaState)}.");

                NfaMatchingState nfaState = state.NfaState!;

                // Swap the current active states set with the scratch set to undo a previous transition.
                SparseIntMap<int> nextStates = nfaState.NfaStateSet;
                SparseIntMap<int> sourceStates = nfaState.NfaStateSetScratch;
                nfaState.NfaStateSet = sourceStates;
                nfaState.NfaStateSetScratch = nextStates;

                // Sanity check: if there are any next states, then there must have been some source states.
                Debug.Assert(nextStates.Count == 0 || sourceStates.Count > 0);
            }
#endif
        }

        /// <summary>
        /// Interface for mapping positions in the input to position IDs, which capture all the information necessary to
        /// both take transitions and decide nullability.
        /// </summary>
        private interface IInputReader
        {
            /// <summary>Gets the position ID for the specified character in the input.</summary>
            /// <remarks>
            /// For positions of valid characters that are handled normally, these IDs coincide with minterm IDs (i.e. indices to <see cref="_minterms"/>).
            /// Positions outside the bounds of the input are mapped to -1. Optionally, an end-of-line as the very last character in the input may be
            /// mapped to _minterms.Length for supporting the \Z anchor. The <paramref name="input"/> and <paramref name="pos"/> parameters are specified
            /// separately, rather than <code>input[pos]</code> being passed in as a single <see cref="char"/>, because some inputs need to act differently
            /// based on the position itself.
            /// </remarks>
            public static abstract int GetPositionId(SymbolicRegexMatcher<TSet> matcher, ReadOnlySpan<char> input, int pos);
        }

        /// <summary>Provides an input reader that includes full handling of an \n as the last character of input for the \Z anchor.</summary>
        private readonly struct DefaultInputReader : IInputReader
        {
            /// <summary>
            /// Gets the minterm ID of the specified character, -1 if the position isn't within the input, or <see cref="_minterms"/>.Length
            /// for a \n at the very end of the input.
            /// </summary>
            public static int GetPositionId(SymbolicRegexMatcher<TSet> matcher, ReadOnlySpan<char> input, int pos)
            {
                if ((uint)pos < (uint)input.Length)
                {
                    // Find the minterm, handling the special case for the last \n for states that start with a relevant anchor
                    int c = input[pos];
                    return c == '\n' && pos == input.Length - 1 ?
                        matcher._minterms.Length : // mintermId = minterms.Length represents an \n at the very end of input
                        matcher._mintermClassifier.GetMintermID(c);
                }

                return -1;
            }
        }

        /// <summary>Provides an optimized input reader that doesn't provide special-handling of \n at the end of the input for the \Z anchor.</summary>
        private readonly struct NoZAnchorOptimizedInputReader : IInputReader
        {
            /// <summary>Gets the minterm ID of the specified character, or -1 if the position isn't within the input.</summary>
            public static int GetPositionId(SymbolicRegexMatcher<TSet> matcher, ReadOnlySpan<char> input, int pos) =>
                (uint)pos < (uint)input.Length ?
                    matcher._mintermClassifier.GetMintermID(input[pos]) :
                    -1;
        }

        /// <summary>Represents a handler used to determine the next possible matching position from an initial state.</summary>
        private interface IInitialStateHandler
        {
            /// <summary>Gets whether the handler performs any meaningful operation. If false, <see cref="TryFindNextStartingPosition"/> always returns true.</summary>
            /// <remarks>
            /// This should be implemented to always return a constant true or false. The consumer will inline it and, if this is false, can dead-code eliminate
            /// anything guarded by the condition.
            /// </remarks>
            public static abstract bool IsOptimized { get; }

            /// <summary>Gets the next viable starting position.</summary>
            /// <returns>true if a possible match location is found; false if no match is possible anywhere in the remaining input.</returns>
            /// <remarks>This may be used if <see cref="IsOptimized"/> is false but it will then always return true indicating that the current position may be viable.</remarks>
            public static abstract bool TryFindNextStartingPosition(
                SymbolicRegexMatcher<TSet> matcher, ReadOnlySpan<char> input, ref int currentStateId, ref int pos, byte[]? lookup);
        }

        /// <summary>Provides an initial state handler for when there are no initial state optimizations to apply.</summary>
        private readonly struct NoOptimizationsInitialStateHandler : IInitialStateHandler
        {
            /// <summary>Returns false.</summary>
            public static bool IsOptimized => false;

            /// <summary>Returns true. No optimizations are known to be able to skip states, thus every position is a viable starting position.</summary>
            public static bool TryFindNextStartingPosition(
                SymbolicRegexMatcher<TSet> matcher, ReadOnlySpan<char> input, ref int currentStateId, ref int pos, byte[]? lookup) =>
                true;
        }

        /// <summary>Provides a handler that uses the matcher's <see cref="RegexFindOptimizations"/> to optimize searching for the next viable starting state.</summary>
        private readonly struct FindOptimizationsInitialStateHandler : IInitialStateHandler
        {
            /// <summary>Returns true.</summary>
            public static bool IsOptimized => true;

            /// <summary>Gets the next viable starting position.</summary>
            /// <returns>true if a viable starting position was found; false if no further possible match exists.</returns>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool TryFindNextStartingPosition(
                SymbolicRegexMatcher<TSet> matcher, ReadOnlySpan<char> input, ref int currentStateId, ref int pos, byte[]? lookup)
            {
                Debug.Assert(matcher._findOpts is not null);

                // Find the first position that matches with some likely character.
                if (matcher._findOpts.TryFindNextStartingPositionLeftToRight(input, ref pos, 0))
                {
                    // Update the starting state based on where TryFindNextStartingPosition moved us to.
                    // As with the initial starting state, if it's a dead end, no match exists.
                    currentStateId = matcher._dotstarredInitialStates[matcher.GetCharKind(input, pos - 1)].Id;
                    return true;
                }

                // No match exists
                Debug.Assert(pos == input.Length);
                currentStateId = matcher._deadStateId;
                return false;
            }
        }

        /// <summary>Provides a handler that uses the matcher's <see cref="RegexFindOptimizations"/> to optimize searching for the next viable starting state.</summary>
        /// <remarks>This implementation works only when there are no /Z anchors in the pattern.</remarks>
        private readonly struct NoZAnchorFindOptimizationsInitialStateHandler : IInitialStateHandler
        {
            /// <summary>Returns true.</summary>
            public static bool IsOptimized => true;

            /// <summary>Gets the next viable starting position.</summary>
            /// <returns>true if a viable starting position was found; false if no further possible match exists.</returns>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool TryFindNextStartingPosition(
                SymbolicRegexMatcher<TSet> matcher, ReadOnlySpan<char> input, ref int currentStateId, ref int pos, byte[]? lookup)
            {
                Debug.Assert(matcher._findOpts is not null);
                Debug.Assert(lookup is not null, $"{nameof(NoZAnchorFindOptimizationsInitialStateHandler)} must only be used with call sites that pass non-null {nameof(lookup)}.");

                if (matcher._findOpts.TryFindNextStartingPositionLeftToRight(input, ref pos, 0))
                {
                    // Update the starting state based on where TryFindNextStartingPosition moved us to.
                    // This is an optimized version of the update in FindOptimizationsInitialStateHandler that doesn't need to consider the possibility of /Z anchors.
                    currentStateId = matcher._dotstarredInitialStates[matcher._positionKinds[GetMintermId(lookup, input[pos - 1]) + 1]].Id;
                    return true;
                }

                // No match exists
                Debug.Assert(pos == input.Length);
                currentStateId = matcher._deadStateId;
                return false;
            }
        }

        /// <summary>Provides a handler that uses the matcher's <see cref="RegexFindOptimizations"/> to optimize searching for the next viable starting state.</summary>
        /// <remarks>This implementation works only when there are no anchors in the pattern.</remarks>
        private readonly struct NoAnchorsFindOptimizationsInitialStateHandler : IInitialStateHandler
        {
            /// <summary>Returns true.</summary>
            public static bool IsOptimized => true;

            /// <summary>Gets the next viable starting position.</summary>
            /// <returns>true if a viable starting position was found; false if no further possible match exists.</returns>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool TryFindNextStartingPosition(
                SymbolicRegexMatcher<TSet> matcher, ReadOnlySpan<char> input, ref int currentStateId, ref int pos, byte[]? lookup)
            {
                Debug.Assert(!matcher._containsAnyAnchor);
                Debug.Assert(matcher._findOpts is not null);
                Debug.Assert(currentStateId == matcher._initialStateId, "There are no anchors, so the current state should be the sole initial state.");

                if (matcher._findOpts.TryFindNextStartingPositionLeftToRight(input, ref pos, 0))
                {
                    // There are no anchors, so there's only one starting state, so we don't need to update currentStateId that's already the starting state.
                    return true;
                }

                // No match exists
                Debug.Assert(pos == input.Length);
                currentStateId = matcher._deadStateId;
                return false;
            }
        }

        /// <summary>Represents a handler for evaluating nullability of states.</summary>
        private interface INullabilityHandler
        {
            /// <summary>Gets whether the specified position is nullable.</summary>
            public static abstract bool IsNullableAt<TStateHandler>(
                SymbolicRegexMatcher<TSet> matcher, in CurrentState state, int positionId)
                where TStateHandler : struct, IStateHandler;
        }

        /// <summary>Nullability handler that will work for any pattern.</summary>
        private readonly struct DefaultNullabilityHandler : INullabilityHandler
        {
            /// <summary>Gets whether the specified position is nullable.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool IsNullableAt<TStateHandler>(SymbolicRegexMatcher<TSet> matcher, in CurrentState state, int positionId)
                where TStateHandler : struct, IStateHandler
            {
                StateFlags flags = TStateHandler.GetStateFlags(matcher, in state);
                return
                    flags.IsNullable() ||
                    (flags.CanBeNullable() && TStateHandler.IsNullableFor(matcher, in state, matcher.GetPositionKind(positionId)));
            }
        }

        /// <summary>Nullability handler for patterns without any anchors.</summary>
        private readonly struct NoAnchorsNullabilityHandler : INullabilityHandler
        {
            /// <summary>Gets whether the specified position is nullable.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool IsNullableAt<TStateHandler>(SymbolicRegexMatcher<TSet> matcher, in CurrentState state, int positionId)
                where TStateHandler : struct, IStateHandler
            {
                Debug.Assert(!matcher._pattern._info.ContainsSomeAnchor);
                return TStateHandler.GetStateFlags(matcher, in state).IsNullable();
            }
        }

        /// <summary>Represents a handler for evaluating nullability of states and for use in DFAs for patterns that do not contain \Z anchors.</summary>
        private interface IDfaNoZAnchorOptimizedNullabilityHandler
        {
            /// <summary>Gets whether the specified position is nullable.</summary>
            public static abstract bool IsNullable(SymbolicRegexMatcher<TSet> matcher, byte stateNullability, char c, byte[] lookup);
        }

        /// <summary>Optimized nullability handler that works regardless of what additional anchors may exist in a pattern.</summary>
        private readonly struct DefaultDfaNoZAnchorOptimizedNullabilityHandler : IDfaNoZAnchorOptimizedNullabilityHandler
        {
            /// <summary>Gets whether the specified position is nullable.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool IsNullable(SymbolicRegexMatcher<TSet> matcher, byte stateNullability, char c, byte[] lookup) =>
                stateNullability != 0 &&
                matcher.IsNullableWithContext(stateNullability, c < (uint)lookup.Length ? lookup[c] : 0);
        }

        /// <summary>Optimized nullability handler for when a pattern has no anchors at all.</summary>
        private readonly struct NoAnchorDfaOptimizedNullabilityHandler : IDfaNoZAnchorOptimizedNullabilityHandler
        {
            /// <summary>Gets whether the specified position is nullable.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool IsNullable(SymbolicRegexMatcher<TSet> matcher, byte stateNullability, char c, byte[] lookup)
            {
                Debug.Assert(!matcher._pattern._info.ContainsSomeAnchor);
                return stateNullability != 0;
            }
        }
    }
}
