using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace ProfilerWeaver;

public static class ProfilerWeaver
{
    private const string BeginMethodName = "BeginSample";
    private const string EndMethodName = "EndSample";

    private enum WeaveMethodResult
    {
        Skipped,
        Error,
        Success
    }

    public static void Main(string[] args)
    {
        try
        {
            LogLine($"ProfilerWeaver starting with args: {string.Join(' ', args)}");

            // expect assemblyPath, outputPath, managerClassName
            if (args.Length != 4)
            {
                var message = $"Expected 4 arguments (assemblyPath, outputPath, managerClassName, whitelistNamespaces), " +
                              $"got {args.Length}: {string.Join(", ", args)}";

                // remember to initialize the log before we output error
                InitializeLog(string.Empty);
                LogLine(message);

                throw new ArgumentException(message);
            }

            // validate args
            var assemblyPath = args[0];
            var outputPath = args[1];
            var managerClassName = args[2];
            var whitelistNamespaces = args[3];

            InitializeLog(assemblyPath);

            if (!File.Exists(assemblyPath))
            {
                var message = $"Error: Assembly not found at {assemblyPath}";

                LogLine(message);
                throw new ArgumentException(message);
            }

            if (string.IsNullOrWhiteSpace(managerClassName))
            {
                const string message = "Error: Manager class name cannot be null, empty or whitespace";

                LogLine(message);
                throw new ArgumentException(message);
            }

            var whitelistedNamespaces = whitelistNamespaces.Split(",");

            LogLine($"Beginning weaving of {assemblyPath} using {managerClassName}");
            LogLine($"Whitelisted namespaces: \n\t{string.Join("\n\t", whitelistedNamespaces)}");

            var timer = Stopwatch.StartNew();

            Weave(assemblyPath, outputPath, managerClassName, whitelistedNamespaces);

            timer.Stop();

            LogLine($"Weaving complete, took: {timer.ElapsedMilliseconds}ms");
        }
        catch (Exception e)
        {
            var message = $"Error: Weaving failed: {e.Message}\n{e.StackTrace}";

            Console.WriteLine(message);
            LogLine(message);
        }
        finally
        {
            CloseLog();
        }
    }
    #region Arguments

    private readonly struct Arguments(
        string assemblyPath,
        string outputPath,
        string managerClassName,
        string[] whitelistNamespaces,
        string rawWhitelistNamespaces,
        string beginMethodName,
        string endMethodName,
        bool isValid
    )
    {
        public string AssemblyPath { get; } = assemblyPath;
        public string OutputPath { get; } = outputPath;
        public string ManagerClassName { get; } = managerClassName;
        public string[] WhitelistNamespaces { get; } = whitelistNamespaces;
        public string RawWhitelistNamespaces { get; } = rawWhitelistNamespaces;
        public string BeginMethodName { get; } = beginMethodName;
        public string EndMethodName { get; } = endMethodName;
        public bool IsValid { get; } = isValid;
    }

    /// <summary>
    /// Responsible for validating command line arguments and initializing the log according to argument validity
    /// </summary>
    private static Arguments ValidateArgs(string[] args)
    {
        const int expectedArgs = 6;

        if (args.Length != expectedArgs)
        {
            // remember to initialize the log before we output error
            InitializeLog(string.Empty);
            LogLine(
                $"Expected {expectedArgs} arguments in order:",
                $"\t- assemblyPath",
                $"\t- outputPath",
                $"\t- managerClassName",
                $"\t- whitelistNamespaces",
                $"\t- beginMethodName",
                $"\t- endMethodName",
                $"\nGot {args.Length}: {string.Join(", ", args)}"
            );

            return default;
        }

        // validate args
        var assemblyPath = args[0];
        var outputPath = args[1];
        var managerClassName = args[2];
        var rawWhitelistNamespaces = args[3];
        var beginMethodName = args[4];
        var endMethodName = args[5];

        InitializeLog(assemblyPath);

        if (!File.Exists(assemblyPath))
        {
            LogLine($"Error: Assembly not found at {assemblyPath}");
            return default;
        }

        if (!Directory.Exists(Path.GetDirectoryName(outputPath)))
        {
            LogLine($"Error: Output directory not found at {Path.GetDirectoryName(outputPath)}");
            return default;
        }

        if (string.IsNullOrWhiteSpace(managerClassName))
        {
            LogLine("Error: Manager class name cannot be null, empty or whitespace");
            return default;
        }

        if (string.IsNullOrWhiteSpace(beginMethodName))
        {
            LogLine("Error: Begin method name cannot be null, empty or whitespace");
            return default;
        }

        if (string.IsNullOrWhiteSpace(endMethodName))
        {
            LogLine("Error: End method name cannot be null, empty or whitespace");
            return default;
        }

        var whitelistedNamespaces = rawWhitelistNamespaces.Split(",");

        return new Arguments(assemblyPath, outputPath, managerClassName, whitelistedNamespaces, rawWhitelistNamespaces,
            beginMethodName, endMethodName, true);
    }

    #endregion

    #region Logging

    private static FileStream? _logStream;

    // let's use 256 lines of buffered log data so we don't overuse memory
    private static readonly BlockingCollection<string> BufferedLogLines = new(256);
    private static CancellationToken _loggingTaskCancellation;
    private static Task? _loggingTask;

    private static int _indentLevel;

    private static void IncreaseIndent() => _indentLevel += 1;
    private static void DecreaseIndent() => _indentLevel = Math.Max(0, _indentLevel - 1);

    private static string GetLogFileName(string assemblyPath)
    {
        const string defaultName = "ProfilerWeaver";

        if (string.IsNullOrWhiteSpace(assemblyPath))
            return $"{defaultName}.log";

        var assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);

        return $"{defaultName}-{assemblyName}.log";
    }

    private static string GetLogDirectory()
    {
        return Path.GetDirectoryName(
            Assembly.GetExecutingAssembly().Location
        ) ?? string.Empty;
    }

    private static void InitializeLog(string assemblyPath)
    {
        var logLocation = Path.Combine(GetLogDirectory(), GetLogFileName(assemblyPath));

        Console.WriteLine($"Creating log file at: {logLocation}");

        _logStream = File.Create(logLocation);
    }

    private static void CloseLog()
    {
        BufferedLogLines.CompleteAdding();
        _loggingTask?.Wait();

        BufferedLogLines.Dispose();

        _logStream?.Close();
        _logStream?.Dispose();
    }

    /// <summary>
    /// Logs messages to the console and the log file
    /// </summary>
    /// <param name="messages">Messages to log, each on a new line</param>
    private static void LogLine(params string[] messages)
    {
        if (_loggingTaskCancellation.IsCancellationRequested)
            return;

        var indentString = string.Join("", Enumerable.Repeat("\t", _indentLevel));

        foreach (var message in messages)
        {
            // apply indent to newlines in message
            var indentedMessage = indentString + message.Replace("\n", $"\n{indentString}");

            BufferedLogLines.Add($"{indentedMessage}\n");
            Console.WriteLine(indentedMessage);
        }

        if (_loggingTask != null)
            return;

        _loggingTaskCancellation = CancellationToken.None;

        _loggingTask = Task.Run(() =>
        {
            try
            {
                // This is the idiomatic and correct way to create a consumer.
                // It blocks until an item is available or the collection is marked as complete.
                foreach (var nextLine in BufferedLogLines.GetConsumingEnumerable(_loggingTaskCancellation))
                {
                    // Use UTF8 for better compatibility and get the correct byte count.
                    var bytes = Encoding.UTF8.GetBytes(nextLine);
                    _logStream?.Write(bytes, 0, bytes.Length);
                }
            }
            catch (OperationCanceledException)
            {
                // This is expected if the task is cancelled. The loop will terminate.
            }
            finally
            {
                // Ensure any OS-level buffers are flushed to disk before the task exits.
                _logStream?.Flush();
            }
        }, _loggingTaskCancellation);
    }

    /// <summary>
    /// Dumps the full IL of a method body to the log for debugging purposes
    /// </summary>
    private static void LogMethodBodyDump(MethodDefinition method)
    {
        IncreaseIndent();
        LogLine($"--- BEGIN IL DUMP FOR {method.FullName} ---");

        var body = method.Body;

        if (body.Variables.Count != 0)
        {
            LogLine("Variables:");
            IncreaseIndent();
            foreach (var variable in body.Variables)
            {
                LogLine($"[{variable.Index}] {variable.VariableType.FullName}");
            }
            DecreaseIndent();
        }

        if (body.ExceptionHandlers.Count != 0)
        {
            LogLine("Exception Handlers:");
            IncreaseIndent();
            foreach (var handler in body.ExceptionHandlers)
            {
                LogLine(
                    $"{handler.HandlerType}:",
                    $"\tTryStart: IL_{handler.TryStart?.Offset:X4}",
                    $"\tTryEnd: IL_{handler.TryEnd?.Offset:X4}",
                    $"\tHandlerStart: IL_{handler.HandlerStart?.Offset:X4}",
                    $"\tHandlerEnd: IL_{handler.HandlerEnd?.Offset:X4}"
                );
            }
            DecreaseIndent();
        }

        LogLine("Instructions:");
        IncreaseIndent();
        foreach (var instruction in body.Instructions)
        {
            var operandString = "";
            if (instruction.Operand is Instruction targetInstruction)
            {
                operandString = $"IL_{targetInstruction.Offset:X4}";
            }
            else if (instruction.Operand is Instruction[] targetInstructions)
            {
                operandString = $"({string.Join(", ", targetInstructions.Select(i => $"IL_{i.Offset:X4}"))})";
            }
            else if (instruction.Operand != null)
            {
                operandString = instruction.Operand.ToString();
            }

            LogLine($"IL_{instruction.Offset:X4}: {instruction.OpCode.Name} {operandString}");
        }
        DecreaseIndent();

        LogLine($"--- END IL DUMP FOR {method.FullName} ---");
        DecreaseIndent();
    }

    #endregion

    private static MethodDefinition? GetBeginMethod(TypeDefinition type)
    {
        return type.Methods.FirstOrDefault(
            m => m.Name == BeginMethodName && m.Parameters.Count == 1
                                         && m.Parameters[0].ParameterType.FullName == "System.String"
        );
    }

    private static MethodDefinition? GetEndMethod(TypeDefinition type)
    {
        return type.Methods.FirstOrDefault(m => m.Name == EndMethodName && m.Parameters.Count == 0);
    }

    private static void Weave(string assemblyPath, string outputPath, string managerClassName,
        string[] whitelistNamespaces)
    {
        var resolver = new DefaultAssemblyResolver();

        resolver.AddSearchDirectory(Path.GetDirectoryName(assemblyPath));

        var readParameters = new ReaderParameters
        {
            AssemblyResolver = resolver
        };

        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        var tryReadSymbols = File.Exists(pdbPath);

        if (tryReadSymbols)
        {
            readParameters.ReadSymbols = true;
            LogLine($"Found PDB file for input assembly: {pdbPath}. Attempting to read symbols.");
        }
        else
        {
            LogLine($"No PDB file found for input assembly: {pdbPath}. Skipping symbol reading.");
        }

        var writeParameters = new WriterParameters()
        {
            WriteSymbols = true
        };

        AssemblyDefinition? assembly = null; // Initialize to null

        try
        {
            try
            {
                assembly = AssemblyDefinition.ReadAssembly(assemblyPath, readParameters);
            }
            catch (Exception ex)
            {
                // Check for the specific "symbols not matching" error
                if (tryReadSymbols && ex.Message.Contains("Symbols were found but are not matching the assembly"))
                {
                    LogLine($"WARNING: Symbols for '{assemblyPath}' were found but do not match the assembly");
                    LogLine($"Attempting to re-read assembly WITHOUT symbols. Original error: {ex.Message}");

                    // Re-attempt to read without symbols
                    readParameters.ReadSymbols = false; // Disable symbol reading
                    assembly = AssemblyDefinition.ReadAssembly(assemblyPath, readParameters); // Try reading again
                }
                else
                {
                    // Re-throw if it's a different kind of error
                    LogLine($"FATAL ERROR: Unexpected error while reading assembly '{assemblyPath}'. Error: {ex.Message}");
                    throw;
                }
            }

            // If assembly is still null here, something went wrong in the catch block
            if (assembly == null)
            {
                throw new InvalidOperationException($"Failed to load assembly '{assemblyPath}' after multiple attempts.");
            }

            var mainModule = assembly.MainModule;

            var profilerManagerType = mainModule.Types.FirstOrDefault(t => t.FullName == managerClassName);
            if (profilerManagerType == null)
            {
                var message = $"Error: Could not find {managerClassName} type in {assemblyPath}";

                LogLine(message);
                throw new ArgumentException(message);
            }

            LogLine($"Profiler manager type {managerClassName} successfully resolved: {profilerManagerType}");

            // Get method references
            var beginSampleMethodDef = GetBeginMethod(profilerManagerType);
            var endSampleMethodDef = GetEndMethod(profilerManagerType);

            if (beginSampleMethodDef == null || endSampleMethodDef == null)
            {
                const string message = "Error: Could not find BeginSample or EndSample methods in YourProfilerManager.";

                LogLine(message);
                throw new ArgumentException(message);
            }

            LogLine($"Begin and End method definitions successfully resolved: {beginSampleMethodDef}, {endSampleMethodDef}");

            // Import references into the current module
            var beginSampleMethodRef = mainModule.ImportReference(beginSampleMethodDef);
            var endSampleMethodRef = mainModule.ImportReference(endSampleMethodDef);

            LogLine($"Begin and End method references successfully imported: {beginSampleMethodRef}, {endSampleMethodRef}");
            LogLine("Beginning method weaving...");

            IncreaseIndent();

            for (var moduleIndex = 0; moduleIndex < assembly.Modules.Count; moduleIndex++)
            {
                var module = assembly.Modules[moduleIndex];

                LogLine($"Weaving module {module.Name} ({moduleIndex}/{assembly.Modules.Count})");

                IncreaseIndent();

                for (var typeIndex = 0; typeIndex < module.Types.Count; typeIndex++)
                {
                    var type = module.Types[typeIndex];

                    // avoid self-profiling
                    if (type == profilerManagerType)
                        continue;

                    // check whitelist
                    if (!whitelistNamespaces.Any(n => type.Namespace.StartsWith(n)))
                    {
                        LogLine($"Type {type.FullName} in namespace {type.Namespace} is not whitelisted, skipping");
                        continue;
                    }

                    if (type.CustomAttributes.Any(a => a.AttributeType.FullName.Contains("BurstCompile")))
                    {
                        LogLine($"Type {type.FullName} is Burst compiled, skipping");
                        continue;
                    }

                    LogLine($"Weaving type {type.FullName} in namespace {type.Namespace} " +
                            $"({typeIndex}/{module.Types.Count})");

                    IncreaseIndent();

                    for (var methodIndex = 0; methodIndex < type.Methods.Count; methodIndex++)
                    {
                        var method = type.Methods[methodIndex];

                        LogLine($"Weaving method {method.Name} in type {type.FullName} " +
                                $"({methodIndex}/{type.Methods.Count})");

                        var result = WeaveMethod(method, beginSampleMethodRef, endSampleMethodRef);

                        if (result == WeaveMethodResult.Error)
                        {
                            throw new Exception($"Failed to weave method {method.FullName}");
                        }

                        LogLine(string.Join("", Enumerable.Repeat("=", 80)));
                    }

                    DecreaseIndent();
                }

                DecreaseIndent();
            }

            DecreaseIndent();

            LogLine($"Weaving for assembly {assemblyPath} completed, writing to {outputPath}. Include symbols " +
                    $"{writeParameters.WriteSymbols}");

            assembly.Write(outputPath, writeParameters);
        }
        finally
        {
            assembly?.Dispose();
        }
    }

    private static WeaveMethodResult WeaveMethod(MethodDefinition method,
        MethodReference beginSampleMethodRef, MethodReference endSampleMethodRef)
    {
        try
        {
            // skip no body, abstract, or native code
            if (!method.HasBody || method.Body == null || method.Body.Instructions.Count == 0
                                || method.IsAbstract || method.IsPInvokeImpl)
            {
                LogLine($"Skipping method {method.FullName} (no body/abstract/external)");
                return WeaveMethodResult.Skipped;
            }

            // skip constructors and platform invoke
            if (method.IsConstructor || method.IsPInvokeImpl)
            {
                LogLine($"Skipping constructor/platform invoke for {method.DeclaringType.Name}");
                return WeaveMethodResult.Skipped;
            }

            // skip compiler-generated state machines (async/await, iterators/yield)
            if (method.CustomAttributes.Any(a =>
                    a.AttributeType.FullName == typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute).FullName ||
                    a.AttributeType.FullName == typeof(System.Runtime.CompilerServices.IteratorStateMachineAttribute).FullName))
            {
                LogLine($"Skipping state machine method {method.FullName} (async/await or iterator)");
                return WeaveMethodResult.Skipped;
            }

            // don't mess with existing handlers, this gets messy
            if (method.Body.HasExceptionHandlers)
            {
                LogLine($"Skipping method {method.FullName} because it already contains exception handlers.");
                return WeaveMethodResult.Skipped;
            }

            LogLine($"Weaving method {method.FullName} with a robust IN-PLACE try...finally block.");

            var body = method.Body;
            var il = body.GetILProcessor();

            var originalFirstInstruction = body.Instructions.First();
            var originalReturns = body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();

            // cache the return variable if needed.
            VariableDefinition? returnDefinition = null;
            if (method.ReturnType.FullName != typeof(void).FullName)
            {
                returnDefinition = new VariableDefinition(method.ReturnType);
                body.Variables.Add(returnDefinition);
            }

            // inject begin sample call
            il.InsertBefore(originalFirstInstruction, il.Create(OpCodes.Ldstr, $"{method.DeclaringType.FullName}.{method.Name}"));
            il.InsertBefore(originalFirstInstruction, il.Create(OpCodes.Call, beginSampleMethodRef));

            // inject finally block
            var finallyStart = il.Create(OpCodes.Call, endSampleMethodRef);
            il.Append(finallyStart);
            il.Append(il.Create(OpCodes.Endfinally));

            // keep track of where the finally end should live
            var finallyEnd = il.Create(OpCodes.Nop);
            il.Append(finallyEnd);

            // we might need to cache a return value
            if (returnDefinition != null)
            {
                il.Append(il.Create(OpCodes.Ldloc, returnDefinition));
            }

            il.Append(il.Create(OpCodes.Ret));

            // rework returns to jump into finally, also cache any return value
            foreach (var ret in originalReturns)
            {
                if (returnDefinition != null)
                {
                    il.InsertBefore(ret, il.Create(OpCodes.Stloc, returnDefinition));
                }

                // mutate return to jump into finally
                ret.OpCode = OpCodes.Leave;
                ret.Operand = finallyEnd;
            }

            // register the try/finally block with the metadata
            var handler = new ExceptionHandler(ExceptionHandlerType.Finally)
            {
                TryStart = originalFirstInstruction,
                TryEnd = finallyStart, // The 'try' block ends just before the 'finally' block. This is the fix.
                HandlerStart = finallyStart,
                HandlerEnd = finallyEnd // The handler's scope ends just before our new return sequence.
            };

            body.ExceptionHandlers.Add(handler);

            // fix offsets
            body.OptimizeMacros();

            LogLine($"Successfully wove {method.Name}.");
            LogMethodBodyDump(method);
        }
        catch (Exception ex)
        {
            LogLine(
                "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!",
                $"CRITICAL WARNING: Failed to process method '{method.FullName}' due to an error.",
                "This can happen if the method has a structure that the weaver's underlying library (Mono.Cecil) cannot parse.",
                "The method will be SKIPPED, and weaving will continue.",
                $"Underlying Exception: {ex.Message}\n{ex.StackTrace}",
                "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!"
            );

            return WeaveMethodResult.Error;
        }

        return WeaveMethodResult.Success;
    }
}