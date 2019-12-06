// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Globalization;
using System.IO;
using System.Linq;
using BuildXL.Engine.Cache.Serialization;
using BuildXL.Pips.Operations;
using BuildXL.Scheduler.Fingerprints;
using BuildXL.Utilities;
using Newtonsoft.Json.Linq;
using static BuildXL.Scheduler.Tracing.FingerprintStore;

namespace BuildXL.Scheduler.Tracing
{
    /// <summary>
    /// Helper class for reading fingerprint store entries and performing post-retrieval formatting and logging.
    /// </summary>
    public sealed class FingerprintStoreReader : IDisposable
    {
        /// <summary>
        /// The underlying <see cref="FingerprintStore"/> for finer-grained access to data.
        /// </summary>
        public FingerprintStore Store { get; private set; }

        /// <summary>
        /// Directory for outputting individual pip information.
        /// </summary>
        private readonly string m_outputDirectory;

        /// <summary>
        /// Version of the store opened.
        /// </summary>
        public int StoreVersion => Store.StoreVersion;

        /// <summary>
        /// Constructor helper method
        /// </summary>
        public static Possible<FingerprintStoreReader> Create(string storeDirectory, string outputDirectory)
        {
            var possibleStore = FingerprintStore.Open(storeDirectory, readOnly: true);
            if (possibleStore.Succeeded)
            {
                return new FingerprintStoreReader(possibleStore.Result, outputDirectory);
            }

            return possibleStore.Failure;
        }

        private FingerprintStoreReader(FingerprintStore store, string outputDirectory)
        {
            Contract.Requires(store != null);
            Contract.Requires(!string.IsNullOrEmpty(outputDirectory));

            Store = store;
            m_outputDirectory = outputDirectory;
            Directory.CreateDirectory(m_outputDirectory);
        }

        /// <summary>
        /// Calls through to <see cref="FingerprintStore.TryGetCacheMissList(out IReadOnlyList{PipCacheMissInfo})"/>.
        /// </summary>
        public bool TryGetCacheMissList(out IReadOnlyList<PipCacheMissInfo> cacheMissList)
        {
            return Store.TryGetCacheMissList(out cacheMissList);
        }

        /// <summary>
        /// While the returned <see cref="PipRecordingSession"/> is in scope,
        /// records all the information retrieved from the <see cref="FingerprintStore"/>
        /// to per-pip files in <see cref="m_outputDirectory"/>.
        /// </summary>
        public PipRecordingSession StartPipRecordingSession(Process pip, string pipUniqueOutputHash)
        {
            TextWriter writer = new StreamWriter(Path.Combine(m_outputDirectory, pip.SemiStableHash.ToString("x16", CultureInfo.InvariantCulture) + ".txt"));
            Store.TryGetFingerprintStoreEntry(pipUniqueOutputHash, pip.FormattedSemiStableHash, out var entry);

            return new PipRecordingSession(Store, entry, writer);
        }

        /// <summary>
        /// While the returned <see cref="PipRecordingSession"/> is in scope,
        /// records all the information retrieved from the <see cref="FingerprintStore"/>
        /// to per-pip files in <see cref="m_outputDirectory"/>.
        /// </summary>
        public PipRecordingSession StartPipRecordingSession(string pipFormattedSemistableHash)
        {
            TextWriter writer = new StreamWriter(Path.Combine(m_outputDirectory, pipFormattedSemistableHash + ".txt"));
            Store.TryGetFingerprintStoreEntryBySemiStableHash(pipFormattedSemistableHash, out var entry);

            return new PipRecordingSession(Store, entry, writer);
        }

        /// <summary>
        /// Dispose
        /// </summary>
        public void Dispose()
        {
            Store.Dispose();
        }

        /// <summary>
        /// Encapsulates reading entries for one specific pip from the fingerprint store and writing the 
        /// retrieved entries to a records file.
        /// </summary>
        public class PipRecordingSession : IDisposable
        {
            private readonly FingerprintStoreEntry m_entry;

            private readonly FingerprintStore m_store;

            /// <summary>
            /// The formatted semi stable hash of the pip during the build that logged <see cref="m_store"/>.
            /// Formatted semi stable hashes may not be stable for the same pip across different builds.
            /// </summary>
            public string FormattedSemiStableHash => EntryExists ? m_entry.PipToFingerprintKeys.Key : null;

            /// <summary>
            /// The optional writer for the pip entry
            /// </summary>
            public TextWriter PipWriter { get; private set; }

            /// <summary>
            /// Whether the entry exists
            /// </summary>
            public bool EntryExists => m_entry != null;

            /// <summary>
            /// Weak fingerprint of the entry
            /// </summary>
            public string WeakFingerprint
            {
                get
                {
                    Contract.Assert(EntryExists);
                    return m_entry.WeakFingerprintToInputs.Key;
                }
            }

            /// <summary>
            /// Strong fingerprint of the entry
            /// </summary>
            public string StrongFingerprint
            {
                get
                {
                    Contract.Assert(EntryExists);
                    return m_entry.StrongFingerprintEntry.StrongFingerprintToInputs.Key;
                }
            }

            /// <summary>
            /// Path set hash of the entry.
            /// </summary>
            public string PathSetHash
            {
                get
                {
                    Contract.Assert(EntryExists);
                    return m_entry.StrongFingerprintEntry.PathSetHashToInputs.Key;
                }
            }

            /// <summary>
            /// Get path set value of the entry.
            /// </summary>
            public string PathSetValue
            {
                get
                {
                    Contract.Assert(EntryExists);
                    return m_entry.StrongFingerprintEntry.PathSetHashToInputs.Value;
                }
            }

            /// <summary>
            /// Constructor
            /// </summary>
            public PipRecordingSession(FingerprintStore store, FingerprintStoreEntry entry, TextWriter textWriter = null)
            {
                m_store = store;
                m_entry = entry;

                PipWriter = textWriter;

                if (EntryExists && textWriter != null)
                {
                    // Write all pip fingerprint information to a file, except for directory memberships.
                    // Directory memberships are skipped unless there is a strong fingerprint miss
                    // to avoid parsing the strong fingerprint entry.
                    m_entry.Print(PipWriter);
                }
            }

            /// <summary>
            /// Get weak fingerprint tree for the entry
            /// </summary>
            public JsonNode GetWeakFingerprintTree() => JsonTree.Deserialize(m_entry.WeakFingerprintToInputs.Value);

            /// <summary>
            /// Get strong fingerprint tree for the entry
            /// </summary>
            public JsonNode GetStrongFingerprintTree() => MergeStrongFingerprintAndPathSetTrees(GetStrongFingerpintInputTree(), GetPathSetTree());

            /// <summary>
            /// Get pathset tree.
            /// </summary>
            public JsonNode GetPathSetTree() => JsonTree.Deserialize(m_entry.StrongFingerprintEntry.PathSetHashToInputs.Value);

            private JsonNode GetStrongFingerpintInputTree() => JsonTree.Deserialize(m_entry.StrongFingerprintEntry.StrongFingerprintToInputs.Value);

            /// <summary>
            /// Compare pathsets.
            /// </summary>
            public JObject DiffPathSet(PipRecordingSession otherSession)
            {
                if (PathSetHash == otherSession.PathSetHash)
                {
                    // Pathsets are the same.
                    return null;
                }

                JObject result = new JObject();

                // {
                //   PathSetHash: { Old: old_path_set_hash, New: new_path_set_hash }
                // }
                AddPropertyIfNotNull(result, RenderSingleValueDiff("PathSetHash", PathSetHash, otherSession.PathSetHash));

                JsonNode thisPathSetTree = GetPathSetTree();
                JsonNode otherPathSetTree = otherSession.GetPathSetTree();

                JsonNode thisUnsafeOption = JsonTree.FindNodeByName(thisPathSetTree, ObservedPathSet.Labels.UnsafeOptions);
                JsonNode otherUnsafeOption = JsonTree.FindNodeByName(otherPathSetTree, ObservedPathSet.Labels.UnsafeOptions);

                // This is less ideal because we can't see the difference.
                // TODO: dump unsafe option data to the fingerprint store so that we can analyze the content.
                // {
                //   UnsafeOptions: { Old: old_bits, New: new_bits: }
                // }
                AddPropertyIfNotNull(result, RenderSingleValueDiff(ObservedPathSet.Labels.UnsafeOptions, thisUnsafeOption.Values[0], otherUnsafeOption.Values[0]));

                AddPropertyIfNotNull(result, DiffObservedPaths(otherSession));

                JsonNode thisObsFileNameNode = JsonTree.FindNodeByName(thisPathSetTree, ObservedPathSet.Labels.ObservedAccessedFileNames);
                JsonNode otherObsFileNameNode = JsonTree.FindNodeByName(otherPathSetTree, ObservedPathSet.Labels.ObservedAccessedFileNames);

                bool hasDiff = ExtractUnorderedListDiff(thisObsFileNameNode.Values, otherObsFileNameNode.Values, out var addedFileNames, out var removedFileName);

                if (hasDiff)
                {
                    result.Add(new JProperty(
                        ObservedPathSet.Labels.ObservedAccessedFileNames,
                        RenderUnorderedListDiff(addedFileNames, removedFileName, RenderPath)));
                }

                return result;
            }

            /// <summary>
            /// Compare strong fingerprints.
            /// </summary>
            public JObject DiffStrongFingerprint(PipRecordingSession otherSession)
            {
                if (StrongFingerprint == otherSession.StrongFingerprint)
                {
                    return null;
                }

                JObject result = new JObject();

                // {
                //   StrongFingerprint: { Old: old_path_set_hash, New: new_path_set_hash }
                // }
                AddPropertyIfNotNull(result, RenderSingleValueDiff("StrongFingerprint", StrongFingerprint, otherSession.StrongFingerprint));

                AddPropertyIfNotNull(result, DiffObservedPaths(otherSession));

                return result;
            }

            private JProperty DiffObservedPaths(PipRecordingSession otherSession)
            {
                JsonNode thisPathSetTree = GetPathSetTree();
                JsonNode otherPathSetTree = otherSession.GetPathSetTree();

                JsonNode thisPathsNode = JsonTree.FindNodeByName(thisPathSetTree, ObservedPathSet.Labels.Paths);
                JsonNode otherPathsNode = JsonTree.FindNodeByName(otherPathSetTree, ObservedPathSet.Labels.Paths);

                JsonNode thisStrongFingerprintInputTree = JsonTree.FindNodeByName(GetStrongFingerpintInputTree(), ObservedInputConstants.ObservedInputs);
                JsonNode otherStrongFingerprintInputTree = JsonTree.FindNodeByName(otherSession.GetStrongFingerpintInputTree(), ObservedInputConstants.ObservedInputs);

                var thisPathSetData = new Dictionary<string, ObservedInputData>();
                var otherPathSetData = new Dictionary<string, ObservedInputData>();
                traversePathSetPaths(thisPathsNode, thisStrongFingerprintInputTree, thisPathSetData);
                traversePathSetPaths(otherPathsNode, otherStrongFingerprintInputTree, otherPathSetData);

                bool hasDiff = ExtractUnorderedMapDiff(
                    thisPathSetData,
                    otherPathSetData,
                    (thisData, otherData) => thisData.Equals(otherData),
                    out var added,
                    out var removed,
                    out var changed);

                if (hasDiff)
                {
                    // {
                    //   Paths: { 
                    //      Added  : [..paths..],
                    //      Removed: [..paths..],
                    //      Changed: {
                    //        path: { Old: ..., New: ... }
                    //      }: 
                    //   }
                    // }
                    return new JProperty(
                        ObservedPathSet.Labels.Paths,
                        RenderUnorderedMapDiff(
                            thisPathSetData,
                            otherPathSetData,
                            added,
                            removed,
                            changed,
                            RenderPath,
                            (dataA, dataB) => dataA.DescribeDiffWithoutPath(dataB),
                            c => diffDirectoryIfApplicable(c)));
                }

                return null;

                JProperty diffDirectoryIfApplicable(string possiblyChangeDirectory)
                {
                    // {
                    //    Members: {
                    //      Added : [..file..],
                    //      Removed : [..file..]
                    //    }
                    // }
                    const string MembersLabel = "Members";

                    var thisChange = thisPathSetData[possiblyChangeDirectory];
                    var otherChange = otherPathSetData[possiblyChangeDirectory];
                    if (thisChange.AccessType == ObservedInputConstants.DirectoryEnumeration
                        && otherChange.AccessType == ObservedInputConstants.DirectoryEnumeration
                        && thisChange.Pattern == otherChange.Pattern)
                    {
                        if (!TryGetDirectoryMembership(m_store, thisChange.Hash, out var thisMembers))
                        {
                            return new JProperty(MembersLabel, $"{CacheMissAnalysisUtilities.RepeatedStrings.MissingDirectoryMembershipFingerprint} ({nameof(ObservedInputData.Hash)}: {thisChange.Hash})");
                        }

                        if (!TryGetDirectoryMembership(otherSession.m_store, otherChange.Hash, out var otherMembers))
                        {
                            return new JProperty(MembersLabel, $"{CacheMissAnalysisUtilities.RepeatedStrings.MissingDirectoryMembershipFingerprint} ({nameof(ObservedInputData.Hash)}: {otherChange.Hash})");
                        }

                        hasDiff = ExtractUnorderedListDiff(thisMembers, otherMembers, out var addedMembers, out var removedMembers);

                        if (hasDiff)
                        {
                            return new JProperty(MembersLabel, RenderUnorderedListDiff(addedMembers, removedMembers, RenderPath));
                        }
                    }

                    return null;
                }

                void traversePathSetPaths(
                    JsonNode pathSetTree,
                    JsonNode strongFingerprintInputTree,
                    Dictionary<string, ObservedInputData> populatedData)
                {
                    TraversePathSetPaths(pathSetTree, strongFingerprintInputTree, data => populatedData[data.Path] = data);
                }
            }

            /// <summary>
            /// Compares weak fingerprints.
            /// </summary>
            public JObject DiffWeakFingerprint(PipRecordingSession otherSession)
            {
                if (WeakFingerprint == otherSession.WeakFingerprint)
                {
                    return null;
                }

                JObject result = new JObject();

                JsonNode weakFingerprintTree = GetWeakFingerprintTree();
                JsonNode otherWeakFingerprintTree = otherSession.GetWeakFingerprintTree();

                var weakFingerprintData = new Dictionary<string, JsonNode>();
                var otherWeakFingerprintData = new Dictionary<string, JsonNode>();

                JsonTree.VisitTree(weakFingerprintTree, wfNode => weakFingerprintData[wfNode.Name] = wfNode, recurse: false);
                JsonTree.VisitTree(otherWeakFingerprintTree, wfNode => otherWeakFingerprintData[wfNode.Name] = wfNode, recurse: false);

                var fields = new HashSet<string>(weakFingerprintData.Keys.Concat(otherWeakFingerprintData.Keys));

                foreach (var field in fields)
                {
                    bool getFieldNode = weakFingerprintData.TryGetValue(field, out JsonNode fieldNode);
                    bool getOtherFieldNode = otherWeakFingerprintData.TryGetValue(field, out JsonNode otherFieldNode);

                    if (getFieldNode != getOtherFieldNode)
                    {
                        string fieldValue = getFieldNode 
                            ? (fieldNode.Values != null && fieldNode.Values.Count == 1 
                                ? fieldNode.Values[0] 
                                : CacheMissAnalysisUtilities.RepeatedStrings.ExistentValue) 
                            : CacheMissAnalysisUtilities.RepeatedStrings.UnspecifiedValue;
                        string otherFieldValue = getOtherFieldNode 
                            ? (otherFieldNode.Values != null && otherFieldNode.Values.Count == 1 
                                ? otherFieldNode.Values[0] 
                                : CacheMissAnalysisUtilities.RepeatedStrings.ExistentValue)
                            : CacheMissAnalysisUtilities.RepeatedStrings.UnspecifiedValue;

                        AddPropertyIfNotNull(result, RenderSingleValueDiff(field, fieldValue, otherFieldValue));
                    }
                    else if (getFieldNode && getOtherFieldNode)
                    {
                        Contract.Assert(fieldNode != null);
                        Contract.Assert(otherFieldNode != null);

                        AddPropertyIfNotNull(result, DiffWeakFingerprintField(fieldNode, otherFieldNode));
                    }
                }

                return result;
            }

            private static JProperty DiffWeakFingerprintField(JsonNode fieldNode, JsonNode otherFieldNode)
            {
                Contract.Requires(fieldNode != null);
                Contract.Requires(otherFieldNode != null);
                Contract.Requires(fieldNode.Name == otherFieldNode.Name);

                switch (fieldNode.Name)
                {
                    case nameof(Process.Dependencies):
                    {
                        var inputFileData = new Dictionary<string, InputFileData>();
                        var otherInputFileData = new Dictionary<string, InputFileData>();
                        populateInputFileData(fieldNode, inputFileData);
                        populateInputFileData(otherFieldNode, otherInputFileData);
                        return ExtractUnorderedMapDiff(
                            inputFileData,
                            otherInputFileData,
                            (dOld, dNew) => dOld.Equals(dNew),
                            out var added,
                            out var removed,
                            out var changed)
                            ? new JProperty(fieldNode.Name, RenderUnorderedMapDiff(
                                inputFileData,
                                otherInputFileData,
                                added,
                                removed,
                                changed,
                                RenderPath,
                                (dataA, dataB) => dataA.HashOrContent))
                            : null;
                    }

                    case nameof(Process.FileOutputs):
                    {
                        var outputFileData = new Dictionary<string, OutputFileData>();
                        var otherOutputFileData = new Dictionary<string, OutputFileData>();
                        populateOutputFileData(fieldNode, outputFileData);
                        populateOutputFileData(otherFieldNode, otherOutputFileData);
                        return ExtractUnorderedMapDiff(
                            outputFileData,
                            otherOutputFileData,
                            (dOld, dNew) => dOld.Equals(dNew),
                            out var added,
                            out var removed,
                            out var changed)
                            ? new JProperty(fieldNode.Name, RenderUnorderedMapDiff(
                                outputFileData,
                                otherOutputFileData,
                                added,
                                removed,
                                changed,
                                RenderPath,
                                (dataA, dataB) => dataA.Attributes))
                            : null;
                    }

                    case nameof(Process.EnvironmentVariables):
                    {
                        var envVarData = new Dictionary<string, EnvironmentVariableData>();
                        var otherEnvVarData = new Dictionary<string, EnvironmentVariableData>();
                        populateEnvironmentVariableData(fieldNode, envVarData);
                        populateEnvironmentVariableData(otherFieldNode, otherEnvVarData);
                        return ExtractUnorderedMapDiff(
                            envVarData,
                            otherEnvVarData,
                            (dOld, dNew) => dOld.Equals(dNew),
                            out var added,
                            out var removed,
                            out var changed)
                            ? new JProperty(fieldNode.Name, RenderUnorderedMapDiff(
                                envVarData,
                                otherEnvVarData,
                                added,
                                removed,
                                changed,
                                k => k,
                                (dataA, dataB) => dataA.Value))
                            : null;
                    }

                    case nameof(Process.DirectoryDependencies):
                    case nameof(Process.DirectoryOutputs):
                    case nameof(Process.UntrackedPaths):
                    case nameof(Process.UntrackedScopes):
                    case nameof(Process.PreserveOutputWhitelist):
                    case nameof(Process.SuccessExitCodes):
                    case PipFingerprintField.Process.SourceChangeAffectedInputList:
                    case nameof(Process.ChildProcessesToBreakawayFromSandbox):
                    {
                        var data = fieldNode.Values;
                        var otherData = otherFieldNode.Values;
                        return ExtractUnorderedListDiff(data, otherData, out var added, out var removed)
                            ? new JProperty(fieldNode.Name, RenderUnorderedListDiff(added, removed, RenderPath))
                            : null;
                    }
                    default:
                        return RenderSingleValueDiff(fieldNode.Name, getSingleValueNode(fieldNode), getSingleValueNode(otherFieldNode));

                }

                string getSingleValueNode(JsonNode node) => 
                    node.Values.Count > 0 
                    ? node.Values[0] 
                    : CacheMissAnalysisUtilities.RepeatedStrings.MissingValue;

                void populateInputFileData(JsonNode dependencyNode, Dictionary<string, InputFileData> inputFileData)
                {
                    JsonTree.VisitTree(
                        dependencyNode,
                        node =>
                        {
                            string value = CacheMissAnalysisUtilities.RepeatedStrings.MissingValue;
                            if (node.Values.Count > 0)
                            {
                                value = node.Values[0];
                            }
                            else if (node.Children.First != null
                                && node.Children.First.Value.Name == PipFingerprintField.FileDependency.PathNormalizedWriteFileContent
                                && node.Children.First.Value.Values.Count > 0)
                            {
                                value = node.Children.First.Value.Values[0];
                            }

                            inputFileData[node.Name] = new InputFileData(node.Name, value);
                        },
                        recurse: false);
                }

                void populateOutputFileData(JsonNode outputNode, Dictionary<string, OutputFileData> outputFileData)
                {
                    JsonTree.VisitTree(
                        outputNode,
                        node =>
                        {
                            string value = CacheMissAnalysisUtilities.RepeatedStrings.MissingValue;
                            if (node.Children.First != null
                                && node.Children.First.Value.Name == PipFingerprintField.FileOutput.Attributes
                                && node.Children.First.Value.Values.Count > 0)
                            {
                                value = node.Children.First.Value.Values[0];
                            }

                            outputFileData[node.Name] = new OutputFileData(node.Name, value);
                        },
                        recurse: false);
                }

                void populateEnvironmentVariableData(JsonNode environmentVariableNode, Dictionary<string, EnvironmentVariableData> environmentVariableData)
                {
                    JsonTree.VisitTree(
                        environmentVariableNode,
                        node =>
                        {
                            environmentVariableData[node.Name] = new EnvironmentVariableData(
                                node.Name,
                                node.Values.Count > 0 ? node.Values[0] : CacheMissAnalysisUtilities.RepeatedStrings.MissingValue);
                        },
                        recurse: false);
                }
            }

            private static bool ExtractUnorderedMapDiff<T>(
                IReadOnlyDictionary<string, T> oldData, 
                IReadOnlyDictionary<string, T> newData,
                Func<T, T, bool> equalValue,
                out IReadOnlyList<string> added,
                out IReadOnlyList<string> removed,
                out IReadOnlyList<string> changed)
            {
                bool hasDiff = ExtractUnorderedListDiff(oldData.Keys, newData.Keys, out added, out removed);
                List<string> mutableChanged = null;

                foreach (var kvp in oldData)
                {
                    if (newData.TryGetValue(kvp.Key, out var newValue) && !equalValue(kvp.Value, newValue))
                    {
                        if (mutableChanged == null)
                        {
                            mutableChanged = new List<string>();
                        }

                        mutableChanged.Add(kvp.Key);
                    }
                }

                changed = mutableChanged;

                return hasDiff || (changed != null && changed.Count > 0);
            }

            private static bool ExtractUnorderedListDiff(
                IEnumerable<string> oldData,
                IEnumerable<string> newData,
                out IReadOnlyList<string> added,
                out IReadOnlyList<string> removed)
            {
                added = null;
                removed = null;

                if (newData.Any() || oldData.Any())
                {
                    var newSet = newData.ToHashSet();
                    var oldSet = oldData.ToHashSet();
                    added = newSet.Except(oldSet).ToList();
                    removed = oldSet.Except(newSet).ToList();

                    return added.Count > 0 || removed.Count > 0;
                }

                return false;
            }

            private static JObject RenderUnorderedMapDiff<T>(
                IReadOnlyDictionary<string, T> oldData,
                IReadOnlyDictionary<string, T> newData,
                IReadOnlyList<string> added, 
                IReadOnlyList<string> removed, 
                IReadOnlyList<string> changed,
                Func<string, string> renderKey,
                Func<T, T, string> describeValueDiff,
                Func<string, JProperty> extraDiffChange = null)
            {
                JObject result = RenderUnorderedListDiff(added, removed, renderKey);

                JProperty changedProperty = null;

                if (changed != null && changed.Count > 0)
                {
                    changedProperty = new JProperty(
                        "Changed",
                        new JObject(changed.Select(c => RenderSingleValueDiff(
                            renderKey(c),
                            describeValueDiff(oldData[c], newData[c]),
                            describeValueDiff(newData[c], oldData[c]),
                            extraDiffChange)).ToArray()));
                }

                if (result == null && changedProperty == null)
                {
                    return null;
                }

                if (result == null)
                {
                    result = new JObject();
                }

                if (changedProperty != null)
                {
                    result.Add(changedProperty);
                }
                
                return result;
            }

            private static JObject RenderUnorderedListDiff(
                IReadOnlyList<string> added,
                IReadOnlyList<string> removed,
                Func<string, string> renderItem)
            {
                JProperty addedProperty = added != null && added.Count > 0 ? new JProperty("Added", new JArray(added.Select(a => renderItem(a)).ToArray())) : null;
                JProperty removedProperty = removed != null && removed.Count > 0 ? new JProperty("Removed", new JArray(removed.Select(a => renderItem(a)).ToArray())) : null;

                if (addedProperty == null && removedProperty == null)
                {
                    return null;
                }

                JObject result = new JObject();
                
                addToResult(addedProperty);
                addToResult(removedProperty);

                return result;

                void addToResult(JProperty p)
                {
                    if (p != null)
                    {
                        result.Add(p);
                    }
                }
            }

            private static JProperty RenderSingleValueDiff(string key, string oldValue, string newValue, Func<string, JProperty> extraDiff = null)
            {
                if (oldValue == newValue)
                {
                    return null;
                }

                var diff = new List<JProperty>
                {
                    new JProperty("Old", oldValue),
                    new JProperty("New", newValue)
                };

                if (extraDiff != null)
                {
                    JProperty extra = extraDiff(key);
                    if (extra != null)
                    {
                        diff.Add(extra);
                    }
                }
                return new JProperty(key, new JObject(diff.ToArray())); 
            }

            private static string RenderPath(string path) => path;

            private static void AddPropertyIfNotNull(JObject o, JProperty p)
            {
                if (p != null)
                {
                    o.Add(p);
                }
            }

            /// <summary>
            /// Path set hash inputs are stored separately from the strong fingerprint inputs.
            /// This merges the path set hash inputs tree into the strong fingerprint inputs tree
            /// while maintaining the 1:1 relationship between the path set and observed inputs.
            /// 
            /// Node notation:
            /// [id] "{name}":"{value}"
            /// Tree notation:
            /// {parentNode}
            ///     {childNode}
            /// 
            /// Start with the following subtrees:
            /// 
            /// From strong fingerprint
            /// 
            /// [1] "PathSet":"VSO0:7E2E49845EC0AE7413519E3EE605272078AF0B1C2911C021681D1D9197CC134A00"
            /// [2] "ObservedInputs":""
            ///     [3] "E":"VSO0:E0C5007DC8CF2D331236F156F136C50CACE2A5D549CD132D9B44ABD1F13D50CC00",
            /// 
            /// From path set hash 
            /// 
            /// [4] "Paths":""
            ///     [5] "Path":"B:/out/objects/n/x/qbkexxlc8je93wycw7yrlw0a305n7k/xunit-out/CacheMissAnaAD836B23/3/obj/readonly/src_0"
            ///     [6] "Flags":"IsDirectoryPath, DirectoryEnumeration, DirectoryEnumerationWithAllPattern"
            ///     [7] "EnumeratePatternRegex":"^.*$"
            ///
            /// And end with:
            /// 
            /// [1] "PathSet":"VSO0:7E2E49845EC0AE7413519E3EE605272078AF0B1C2911C021681D1D9197CC134A00"
            ///     [5'] "B:/out/objects/n/x/qbkexxlc8je93wycw7yrlw0a305n7k/xunit-out/CacheMissAnaAD836B23/3/obj/readonly/src_0":""
            ///         [6'] "Flags":"IsDirectoryPath, DirectoryEnumeration, DirectoryEnumerationWithAllPattern"
            ///         [7'] "EnumeratePatternRegex":"^.*$"
            ///         [3'] "ObservedInput":"E:VSO0:E0C5007DC8CF2D331236F156F136C50CACE2A5D549CD132D9B44ABD1F13D50CC00"
            ///         [8] "Members":"[src_1, src_2]"
            /// </summary>
            /// <returns>
            /// The root node of the merged tree (which will be the strong fingerprint tree's root).
            /// </returns>
            private JsonNode MergeStrongFingerprintAndPathSetTrees(JsonNode strongFingerprintTree, JsonNode pathSetTree)
            {
                // [1] "PathSet":"VSO0:7E2E49845EC0AE7413519E3EE605272078AF0B1C2911C021681D1D9197CC134A00")
                var parentPathNode = JsonTree.FindNodeByName(strongFingerprintTree, ObservedPathEntryConstants.PathSet);

                // [2] "ObservedInputs":""
                var observedInputsNode = JsonTree.FindNodeByName(strongFingerprintTree, ObservedInputConstants.ObservedInputs);
                JsonTree.EmancipateBranch(observedInputsNode);

                // In preparation for merging with observed inputs nodes,
                // remove the path set node's branch from the path set tree
                // [4] "Paths":""
                var pathSetNode = JsonTree.FindNodeByName(pathSetTree, ObservedPathSet.Labels.Paths);
                JsonTree.EmancipateBranch(pathSetNode);
                JsonNode currPathNode = null;
                JsonNode currFlagNode = null;
                JsonNode currRegexNode = null;
                var observedInputIt = observedInputsNode.Children.First;
                for (var it = pathSetNode.Children.First; it != null; it = pathSetNode.Children.First)
                {
                    var child = it.Value;
                    switch (child.Name)
                    {
                        case ObservedPathEntryConstants.Path:
                            if (currPathNode != null)
                            {
                                mergePathSetNode(parentPathNode, currPathNode, currFlagNode, currRegexNode, observedInputIt.Value);
                                observedInputIt = observedInputsNode.Children.First;
                                currPathNode = null;
                                currFlagNode = null;
                                currRegexNode = null;
                            }

                            currPathNode = child;
                            JsonTree.EmancipateBranch(currPathNode);
                            break;
                        case ObservedPathEntryConstants.Flags:
                            // [6] "Flags":"IsDirectoryPath, DirectoryEnumeration, DirectoryEnumerationWithAllPattern"
                            currFlagNode = child;
                            JsonTree.EmancipateBranch(currFlagNode);
                            break;
                        case ObservedPathEntryConstants.EnumeratePatternRegex:
                            // [7] "EnumeratePatternRegex":"^.*$"
                            currRegexNode = child;
                            JsonTree.EmancipateBranch(currRegexNode);
                            break;
                        default:
                            break;
                    }
                }

                if (currPathNode != null)
                {
                    mergePathSetNode(parentPathNode, currPathNode, currFlagNode, currRegexNode, observedInputIt.Value);
                }

                // Re-parent any other branches of the path set tree to the strong fingerprint tree
                // so they are still in a full strong fingerprint tree comparison.
                // We re-parent under parentPathNode because branches of pathSetTree are elements of PathSet
                var node = pathSetTree.Children.First;
                while (node != null)
                {
                    JsonTree.ReparentBranch(node.Value, parentPathNode);
                    node = pathSetTree.Children.First;
                }

                return strongFingerprintTree;

                void mergePathSetNode(JsonNode parentNode, JsonNode pathNode, JsonNode flagNode, JsonNode regexNode, JsonNode observedInputNode)
                {
                    // Switch from literal string "path" to actual file system path
                    // [5'] "B:/out/objects/n/x/qbkexxlc8je93wycw7yrlw0a305n7k/xunit-out/CacheMissAnaAD836B23/3/obj/readonly/src_0":""
                    pathNode.Name = pathNode.Values[0];

                    // The name captures the node's value, so clear the values to avoid extraneous value comparison when diffing
                    pathNode.Values.Clear();
                    JsonTree.ReparentBranch(pathNode, parentNode);

                    // [6'] "Flags":"IsDirectoryPath, DirectoryEnumeration, DirectoryEnumerationWithAllPattern"
                    JsonTree.ReparentBranch(flagNode, pathNode);
                    // [7'] "EnumeratePatternRegex":"^.*$"
                    JsonTree.ReparentBranch(regexNode, pathNode);

                    // [3'] "ObservedInput":"E:VSO0:E0C5007DC8CF2D331236F156F136C50CACE2A5D549CD132D9B44ABD1F13D50CC00"
                    // [8] "Members":"[src_1, src_2]"
                    ReparentObservedInput(observedInputNode, pathNode);
                }
            }

            /// <summary>
            /// Makes a tree that represents an observed input on a path into a subtree of
            /// a tree that represents the corresponding path in the pathset.
            /// 
            /// <see cref="MergeStrongFingerprintAndPathSetTrees(JsonNode, JsonNode)"/>
            /// for numbering explanation.
            /// 
            /// Converts
            /// [3] "E":"VSO0:E0C5007DC8CF2D331236F156F136C50CACE2A5D549CD132D9B44ABD1F13D50CC00"
            /// =>
            /// [3'] "ObservedInput":"E:VSO0:E0C5007DC8CF2D331236F156F136C50CACE2A5D549CD132D9B44ABD1F13D50CC00"
            /// 
            /// Reparent [3'] from
            /// [2] "ObservedInputs":""
            /// to
            /// [5'] "B:/out/objects/n/x/qbkexxlc8je93wycw7yrlw0a305n7k/xunit-out/CacheMissAnaAD836B23/3/obj/readonly/src_0":""
            /// 
            /// Add
            /// [8] "Members":"[src_1, src_2]"
            /// to
            /// [5'] "B:/out/objects/n/x/qbkexxlc8je93wycw7yrlw0a305n7k/xunit-out/CacheMissAnaAD836B23/3/obj/readonly/src_0":""
            /// </summary>
            /// <param name="observedInputNode"></param>
            /// <param name="pathSetNode"></param>
            private void ReparentObservedInput(JsonNode observedInputNode, JsonNode pathSetNode)
            {
                // Store values from
                // [3] "E":"VSO0:E0C5007DC8CF2D331236F156F136C50CACE2A5D549CD132D9B44ABD1F13D50CC00"
                // before manipulating the node
                var observedInputType = observedInputNode.Name;
                var observedInputHash = observedInputNode.Values[0];

                var values = observedInputNode.Values;
                values.Clear();

                string expandedType = ObservedInputConstants.ToExpandedString(observedInputType);
                switch (observedInputType)
                {
                    case ObservedInputConstants.AbsentPathProbe:
                    case ObservedInputConstants.ExistingFileProbe:
                    case ObservedInputConstants.ExistingDirectoryProbe:
                        values.Add(expandedType);
                        break;
                    case ObservedInputConstants.FileContentRead:
                        values.Add($"{expandedType}:{observedInputHash}");
                        break;
                    case ObservedInputConstants.DirectoryEnumeration:
                        values.Add($"{expandedType}:{observedInputHash}");
                        // [8] "Members":"[src_1, src_2]"
                        AddDirectoryMembershipBranch(observedInputHash, pathSetNode);
                        break;
                }

                // [3'] "ObservedInput":"E:VSO0:E0C5007DC8CF2D331236F156F136C50CACE2A5D549CD132D9B44ABD1F13D50CC00"
                observedInputNode.Name = ObservedInputConstants.ObservedInputs;
                JsonTree.ReparentBranch(observedInputNode, pathSetNode);
            }

            /// <summary>
            /// Adds a directory membership tree as a sub-tree to a given path set tree.
            /// </summary>
            /// <param name="directoryFingerprint">
            /// The directory fingerprint to look up membership.
            /// </param>
            /// <param name="pathSetNode">
            /// The path set node that represents the directory and the parent node.
            /// </param>
            private void AddDirectoryMembershipBranch(string directoryFingerprint, JsonNode pathSetNode)
            {
                if (m_store.TryGetContentHashValue(directoryFingerprint, out string inputs))
                {
                    WriteToPipFile(PrettyFormatJsonField(new KeyValuePair<string, string>(directoryFingerprint, inputs)).ToString());

                    var directoryMembershipTree = JsonTree.Deserialize(inputs);
                    for (var it = directoryMembershipTree.Children.First; it != null; it = it.Next)
                    {
                        JsonTree.ReparentBranch(it.Value, pathSetNode);
                    }
                }
                else
                {
                    // Include a node for the directory membership, but use an error message as the value
                    var placeholder = new JsonNode
                    {
                        Name = directoryFingerprint
                    };
                    placeholder.Values.Add(CacheMissAnalysisUtilities.RepeatedStrings.MissingDirectoryMembershipFingerprint);

                    JsonTree.ReparentBranch(placeholder, pathSetNode);
                }
            }

            private static bool TryGetDirectoryMembership(FingerprintStore store, string directoryFingerprint, out IReadOnlyList<string> members)
            {
                members = null;

                if(!store.TryGetContentHashValue(directoryFingerprint, out string storedValue))
                {
                    return false;
                }

                var directoryMembershipTree = JsonTree.Deserialize(storedValue);
                members = directoryMembershipTree.Children.First.Value.Values;
                return true;
            }

            private static void TraversePathSetPaths(
                JsonNode pathSetPathsNode,
                JsonNode observedInputs,
                Action<ObservedInputData> action)
            {
                string path = null;
                string flags = null;
                string pattern = null;

                string hashMarker = null;
                string hash = null;

                var obIt = observedInputs?.Children.First;

                for (var it = pathSetPathsNode.Children.First; it != null; it = it.Next)
                {
                    var elem = it.Value;
                    switch (elem.Name)
                    {
                        case ObservedPathEntryConstants.Path:
                            if (path != null)
                            {
                                action(new ObservedInputData(path, flags, pattern, hashMarker, hash));
                                path = null;
                                flags = null;
                                pattern = null;
                                hashMarker = null;
                                hash = null;
                            }

                            path = elem.Values[0];

                            if (obIt != null)
                            {
                                hashMarker = obIt.Value.Name;
                                hash = obIt.Value.Values[0];
                                obIt = obIt.Next;
                            }

                            break;
                        case ObservedPathEntryConstants.Flags:
                            Contract.Assert(path != null);
                            flags = elem.Values[0];
                            break;
                        case ObservedPathEntryConstants.EnumeratePatternRegex:
                            Contract.Assert(path != null);
                            pattern = elem.Values[0];
                            break;
                        default:
                            break;
                    }
                }

                if (path != null)
                {
                    action(new ObservedInputData(path, flags, pattern, hashMarker, hash));
                }
            }

            private struct ObservedInputData : IEquatable<ObservedInputData>
            {
                public readonly string Path;
                public readonly string Flags;
                public readonly string Pattern;
                public readonly string AccessType;
                public readonly string Hash;

                public ObservedInputData(
                    string path,
                    string flags,
                    string pattern,
                    string hashMarker,
                    string hash)
                {
                    Path = path;
                    Flags = flags;
                    Pattern = pattern;
                    AccessType = hashMarker;
                    Hash = hash;
                }

                public ObservedInputData(string path, string flags, string pattern) : this(path, flags, pattern, null, null) { }

                public bool Equals(ObservedInputData other) =>
                    Path == other.Path && Flags == other.Flags && Pattern == other.Pattern && AccessType == other.AccessType && Hash == other.Hash;

                public override bool Equals(object obj) => StructUtilities.Equals(this, obj);

                public override int GetHashCode()
                {
                    return combine(hashCode(Path), combine(hashCode(Flags), combine(hashCode(Pattern), combine(hashCode(AccessType), hashCode(Hash)))));

                    int hashCode(string s) => s != null ? EqualityComparer<string>.Default.GetHashCode(s) : 0;
                    int combine(int h1, int h2)
                    {
                        unchecked
                        {
                            return ((h1 << 5) + h1) ^ h2;
                        }
                    }
                }

                public string DescribeDiffWithoutPath(ObservedInputData data) =>
                    string.Join(
                        " | ",
                        (new[] {
                            Prefix(nameof(AccessType), ObservedInputConstants.ToExpandedString(AccessType)),
                            Flags == data.Flags ? null : Prefix(nameof(Flags), Flags),
                            Pattern == data.Pattern ? null : Prefix(nameof(Pattern), Pattern),
                            Hash == data.Hash ? null : Prefix(nameof(Hash), Hash) }).Where(s => !string.IsNullOrEmpty(s)));

                private string Prefix(string prefix, string item) => string.IsNullOrEmpty(item) ? null : prefix + ": " + item;
            }

            private struct InputFileData : IEquatable<InputFileData>
            {
                public readonly string Path;
                public readonly string HashOrContent;

                public InputFileData(string path, string hashOrContent)
                {
                    Path = path;
                    HashOrContent = hashOrContent;
                }

                public bool Equals(InputFileData other) => Path == other.Path && HashOrContent == other.HashOrContent;

                public override bool Equals(object obj) => StructUtilities.Equals(this, obj);

                public override int GetHashCode()
                {
                    return combine(hashCode(Path), hashCode(HashOrContent));

                    int hashCode(string s) => s != null ? EqualityComparer<string>.Default.GetHashCode(s) : 0;
                    int combine(int h1, int h2)
                    {
                        unchecked
                        {
                            return ((h1 << 5) + h1) ^ h2;
                        }
                    }
                }
            }

            private struct OutputFileData : IEquatable<OutputFileData>
            {
                public readonly string Path;
                public readonly string Attributes;

                public OutputFileData(string path, string attributes)
                {
                    Path = path;
                    Attributes = attributes;
                }

                public bool Equals(OutputFileData other) => Path == other.Path && Attributes == other.Attributes;

                public override bool Equals(object obj) => StructUtilities.Equals(this, obj);

                public override int GetHashCode()
                {
                    return combine(hashCode(Path), hashCode(Attributes));

                    int hashCode(string s) => s != null ? EqualityComparer<string>.Default.GetHashCode(s) : 0;
                    int combine(int h1, int h2)
                    {
                        unchecked
                        {
                            return ((h1 << 5) + h1) ^ h2;
                        }
                    }
                }
            }

            private struct EnvironmentVariableData : IEquatable<EnvironmentVariableData>
            {
                public readonly string Name;
                public readonly string Value;

                public EnvironmentVariableData(string name, string value)
                {
                    Name = name;
                    Value = value;
                }

                public bool Equals(EnvironmentVariableData other) => Name == other.Name && Value == other.Value;

                public override bool Equals(object obj) => StructUtilities.Equals(this, obj);

                public override int GetHashCode()
                {
                    return combine(hashCode(Name), hashCode(Value));

                    int hashCode(string s) => s != null ? EqualityComparer<string>.Default.GetHashCode(s) : 0;
                    int combine(int h1, int h2)
                    {
                        unchecked
                        {
                            return ((h1 << 5) + h1) ^ h2;
                        }
                    }
                }
            }

            /// <summary>
            /// Writes a message to a specific pip's file.
            /// </summary>
            public void WriteToPipFile(string message)
            {
                PipWriter?.WriteLine(message);
            }

            /// <summary>
            /// Dispose
            /// </summary>
            public void Dispose()
            {
                PipWriter?.Dispose();
            }
        }
    }
}
