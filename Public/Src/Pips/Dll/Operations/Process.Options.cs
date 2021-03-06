// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

namespace BuildXL.Pips.Operations
{
    /// <nodoc />
    public partial class Process
    {
        /// <summary>
        /// Flag options controlling process pip behavior.
        /// </summary>
        [Flags]
        public enum Options : byte
        {
            /// <nodoc />
            None = 0,

            /// <summary>
            /// If set, child processes started by this process are excluded from file access monitoring.
            /// </summary>
            HasUntrackedChildProcesses = 1 << 0,

            /// <summary>
            /// If set, the outputs of this process do not encode a dependency on absolute paths (the absolute paths
            /// of input dependencies can be trimmed based on declared mount points).
            /// </summary>
            ProducesPathIndependentOutputs = 1 << 1,

            /// <summary>
            /// If set, the outputs of this process must be left writable (the build engine may not defensively make them readonly)
            /// This prevents hardlinking of these outputs into the build cache, even if otherwise enabled.
            /// </summary>
            OutputsMustRemainWritable = 1 << 2,

            /// <summary>
            /// If set, allows output from a prior process execution to be left when running the process. This may be
            /// enabled to leverage incremental behavior of the pip. It should only be turned on when the determinism of
            /// the process is trusted
            /// </summary>
            AllowPreserveOutputs = 1 << 3,

            /// <summary>
            /// Light processes go to the light queue.
            /// </summary>
            IsLight = 1 << 4,

            /// <summary>
            /// Whether this process should run in an isolated container
            /// </summary>
            NeedsToRunInContainer = 1 << 5,

            /// <summary>
            /// Whether this process is allowed to read an undeclared source file.
            /// </summary>
            /// <remarks>
            /// A source file is considered to be a file that is not written during a build
            /// </remarks>
            AllowUndeclaredSourceReads = 1 << 6,

            /// <summary>
            /// Whether this process is configured to always be a cache miss.
            /// </summary>
            /// <remarks>
            /// When specified, no cache lookup will be performed for the pip.
            /// </remarks>
            DisableCacheLookup = 1 << 7
        }
    }
}
