// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using BuildXL.Pips.Operations;
using BuildXL.Utilities;
using BuildXL.FrontEnd.Script.Util;
using BuildXL.FrontEnd.Script.Ambients.Transformers;
using BuildXL.FrontEnd.Script;
using BuildXL.FrontEnd.Script.RuntimeModel.AstBridge;
using BuildXL.FrontEnd.Script.Types;
using BuildXL.FrontEnd.Script.Values;
using BuildXL.FrontEnd.Script.Evaluator;
using LineInfo = TypeScript.Net.Utilities.LineInfo;
using BuildXL.Pips.Builders;

namespace BuildXL.FrontEnd.Script.Ambients
{
    /// <summary>
    /// Ambient definition for Debug namespace.
    /// </summary>
    public sealed class AmbientDebug : AmbientDefinitionBase
    {
        // This regular expression matches any string enclosed in curly braces that doesn't contain the '{' character
        private static readonly Regex s_expandPathsRegex = new Regex(@"{([^{]+)}");

        /// <nodoc />
        public AmbientDebug(PrimitiveTypes knownTypes)
            : base("Debug", knownTypes)
        {
        }

        /// <inheritdoc />
        protected override AmbientNamespaceDefinition? GetNamespaceDefinition()
        {
            return new AmbientNamespaceDefinition(
                "Debug",
                new[]
                {
                    Function("launch", Launch, LaunchSignature),
                    Function("sleep", Sleep, SleepSignature), // not currently exposed in prelude, as intended for testing purposes only
                    Function("writeLine", WriteLine, WriteLineSignature),
                    Function("dumpArgs", DumpArgs, DumpArgsSignature),
                    Function("dumpData", DumpData, DumpDataSignature),
                    Function("dumpCallStack", DumpCallStack, DumpCallStackSignature),
                    Function("expandPaths", ExpandPaths, ExpandPathsSignature),
                });
        }

        private static EvaluationResult Launch(Context context, ModuleLiteral env, EvaluationStackFrame args)
        {
            Debugger.Launch();

            return EvaluationResult.Undefined;
        }

        private static EvaluationResult Sleep(Context context, ModuleLiteral env, EvaluationStackFrame args)
        {
            int milliseconds = Args.AsInt(args, 0);

            if (milliseconds >= 0)
            {
                Thread.Sleep(milliseconds);
            }

            return EvaluationResult.Undefined;
        }

        private static EvaluationResult WriteLine(Context context, ModuleLiteral env, EvaluationStackFrame args)
        {
            return WriteToConsoleImpl(context, args);
        }

        private static EvaluationResult WriteToConsoleImpl(Context context, EvaluationStackFrame args)
        {
            string str = string.Join(string.Empty, args.Frame.Select(a => ToStringConverter.ObjectToString(context, a)));

            context.Logger.ScriptDebugLog(context.FrontEndContext.LoggingContext, str);

            return EvaluationResult.Undefined;
        }

        private static EvaluationResult DumpArgs(Context context, ModuleLiteral env, EvaluationStackFrame args)
        {
            var cmdLineArgs = Args.AsArrayLiteral(args, 0);
            var pathTable = context.FrontEndContext.PathTable;

            using (var processBuilder = ProcessBuilder.Create(pathTable, context.FrontEndContext.GetPipDataBuilder()))
            {
                TransformerExecuteArgumentsProcessor.ProcessArguments(context, processBuilder, cmdLineArgs);

                var pipData = processBuilder.ArgumentsBuilder.ToPipData(" ", PipDataFragmentEscaping.NoEscaping);
                return EvaluationResult.Create(pipData.ToString(pathTable));
            }
        }

        private static EvaluationResult DumpData(Context context, ModuleLiteral env, EvaluationStackFrame args)
        {
            var pathTable = context.FrontEndContext.PathTable;
            var data = Args.AsIs(args, 0);

            string dataAsString = null;
            switch (data)
            {
                case string s:
                    dataAsString = s;
                    break;
                case IImplicitPath pathData:
                    dataAsString = pathData.Path.ToString(context.PathTable);
                    break;
                case PathAtom pathAtom:
                    dataAsString = pathAtom.ToString(context.StringTable);
                    break;
                case RelativePath relativePath:
                    dataAsString = relativePath.ToString(context.StringTable);
                    break;
                case int n:
                    dataAsString = n.ToString(CultureInfo.InvariantCulture);
                    break;
                default: // This is effectively only for object literals
                    // Slow path
                    dataAsString = DataProcessor.ProcessData(context, context.FrontEndContext.PipDataBuilderPool, EvaluationResult.Create(data), new ConversionContext(pos: 1)).ToString(context.PathTable);
                    break;

            }

            return EvaluationResult.Create(dataAsString);
        }

        private static EvaluationResult DumpCallStack(Context context, ModuleLiteral env, EvaluationStackFrame args)
        {
            var message = Args.AsStringOptional(args, 0) ?? string.Empty;
            var location = context.TopStack.InvocationLocation.AsUniversalLocation(env, context);
            var stack = context.GetStackTraceAsString(location);

            context.Logger.DebugDumpCallStack(context.FrontEndContext.LoggingContext, location.AsLoggingLocation(), message, stack);
            return EvaluationResult.Undefined;
        }

        private static EvaluationResult ExpandPaths(Context context, ModuleLiteral env, EvaluationStackFrame args)
        {
            var str = Args.AsString(args, 0);
            var pathTable = context.FrontEndContext.PathTable;
            return EvaluationResult.Create(s_expandPathsRegex.Replace(
                str,
                m =>
                {
                    var relPath = RelativePath.Create(context.FrontEndContext.StringTable, m.Groups[1].Value);
                    var absPath = context.LastActiveUsedPath.GetParent(pathTable).Combine(pathTable, relPath);
                    return absPath.ToString(pathTable);
                }));
        }

        private static CallSignature LaunchSignature => CreateSignature(
            required: RequiredParameters(),
            returnType: PrimitiveType.VoidType);

        private static CallSignature SleepSignature => CreateSignature(
            required: RequiredParameters(PrimitiveType.NumberType),
            returnType: PrimitiveType.VoidType);

        private static CallSignature WriteLineSignature => CreateSignature(
            required: RequiredParameters(PrimitiveType.StringType),
            returnType: PrimitiveType.VoidType);

        private CallSignature DumpArgsSignature => CreateSignature(
            required: RequiredParameters(AmbientTypes.ArrayType),
            returnType: PrimitiveType.StringType);

        private CallSignature DumpDataSignature => CreateSignature(
            required: RequiredParameters(AmbientTypes.DataType),
            returnType: PrimitiveType.StringType);

        private static CallSignature DumpCallStackSignature => CreateSignature(
            optional: OptionalParameters(PrimitiveType.StringType),
            returnType: PrimitiveType.VoidType);

        private static CallSignature ExpandPathsSignature => CreateSignature(
            required: RequiredParameters(PrimitiveType.StringType),
            returnType: PrimitiveType.StringType);
    }
}
