﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.Extensions.Configuration.Binder.SourceGeneration
{
    public sealed partial class ConfigurationBindingGenerator
    {
        /// <summary>
        /// Suppresses false-positive diagnostics emitted by the linker
        /// when analyzing binding invocations that we have intercepted.
        /// Workaround for https://github.com/dotnet/roslyn/issues/68669.
        /// </summary>
        [DiagnosticAnalyzer(LanguageNames.CSharp)]
        public sealed class Suppressor : DiagnosticSuppressor
        {
            private const string Justification = "The target method has been intercepted by a generated static variant.";

            /// <summary>
            /// Suppression descriptor for IL2026: Members attributed with RequiresUnreferencedCode may break when trimming.
            /// </summary>
            private static readonly SuppressionDescriptor RUCDiagnostic = new(id: "SYSLIBSUPPRESS0002", suppressedDiagnosticId: "IL2026", Justification);

            /// <summary>
            /// Suppression descriptor for IL3050: Avoid calling members annotated with 'RequiresDynamicCodeAttribute' when publishing as native AOT.
            /// </summary>
            private static readonly SuppressionDescriptor RDCDiagnostic = new(id: "SYSLIBSUPPRESS0003", suppressedDiagnosticId: "IL3050", Justification);

            public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions => ImmutableArray.Create(RUCDiagnostic, RDCDiagnostic);

            public override void ReportSuppressions(SuppressionAnalysisContext context)
            {
                foreach (Diagnostic diagnostic in context.ReportedDiagnostics)
                {
                    string diagnosticId = diagnostic.Id;

                    if (diagnosticId != RDCDiagnostic.SuppressedDiagnosticId && diagnosticId != RUCDiagnostic.SuppressedDiagnosticId)
                    {
                        continue;
                    }

                    Location location = diagnostic.AdditionalLocations.Count > 0
                        ? diagnostic.AdditionalLocations[0]
                        : diagnostic.Location;

                    // The trim analyzer changed from warning on the InvocationExpression to the MemberAccessExpression in https://github.com/dotnet/runtime/pull/110086
                    // In other words, the warning location went from from `{|Method1(arg1, arg2)|}` to `{|Method1|}(arg1, arg2)`
                    // To account for this, we need to check if the location is an InvocationExpression or a child of an InvocationExpression.
                    bool shouldSuppressDiagnostic =
                        location.SourceTree is SyntaxTree sourceTree &&
                        sourceTree.GetRoot().FindNode(location.SourceSpan) is SyntaxNode syntaxNode &&
                        (syntaxNode as InvocationExpressionSyntax ?? syntaxNode.Parent as InvocationExpressionSyntax) is InvocationExpressionSyntax invocation &&
                        BinderInvocation.IsCandidateSyntaxNode(invocation) &&
                        context.GetSemanticModel(sourceTree)
                            .GetOperation(invocation, context.CancellationToken) is IInvocationOperation operation &&
                        BinderInvocation.IsBindingOperation(operation);

                    if (shouldSuppressDiagnostic)
                    {
                        SuppressionDescriptor targetSuppression = diagnosticId == RUCDiagnostic.SuppressedDiagnosticId
                                ? RUCDiagnostic
                                : RDCDiagnostic;
                        context.ReportSuppression(Suppression.Create(targetSuppression, diagnostic));
                    }
                }
            }
        }
    }
}
