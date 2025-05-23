// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Options;

namespace System.Net.Http
{
    /// <summary>
    /// Extensions methods for <see cref="IHttpMessageHandlerFactory"/>.
    /// </summary>
    public static class HttpMessageHandlerFactoryExtensions
    {
        /// <summary>
        /// Creates a new <see cref="HttpMessageHandler"/> using the default configuration.
        /// </summary>
        /// <param name="factory">The <see cref="IHttpMessageHandlerFactory"/>.</param>
        /// <returns>An <see cref="HttpMessageHandler"/> configured using the default configuration.</returns>
        public static HttpMessageHandler CreateHandler(this IHttpMessageHandlerFactory factory)
        {
            ArgumentNullException.ThrowIfNull(factory);

            return factory.CreateHandler(Options.DefaultName);
        }
    }
}
