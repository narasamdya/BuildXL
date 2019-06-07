﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Reflection;
using BuildXL.Storage;
using BuildXL.Storage.ChangeJournalService;
using BuildXL.Utilities;
using BuildXL.Utilities.Instrumentation.Common;

namespace Test.BuildXL.TestUtilities.Xunit
{
    /// <summary>
    /// Journal utilities for unit tests.
    /// </summary>
    public static class JournalUtils
    {
        /// <summary>
        /// Gets an instance of <see cref="IChangeJournalAccessor"/> for tests.
        /// </summary>
        /// <param name="loggingContext">Logging context.</param>
        /// <param name="volumeMap">Volume map.</param>
        /// <returns>An instance of <see cref="IChangeJournalAccessor"/>.</returns>
        public static Optional<IChangeJournalAccessor> TryGetJournalAccessorForTest(LoggingContext loggingContext, VolumeMap volumeMap)
        {
            string path = Environment.GetEnvironmentVariable("[BUILDXL]VM_TEMP");

            if (string.IsNullOrEmpty(path))
            {
                path = AssemblyHelper.GetAssemblyLocation(Assembly.GetExecutingAssembly());
            }

            return JournalAccessorGetter.TryGetJournalAccessor(loggingContext, volumeMap, path);
        }
    }
}