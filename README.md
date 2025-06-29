# Profiler Weaver
A command-line tool for injecting profiling markers into .NET assemblies (at entry/exit points of methods).

## Features

* **Method Weaving**: Injects `BeginSample` and `EndSample` calls around methods in specified namespaces.
* **Configurable Profiler**: Allows specifying a custom profiler manager class and method names.
* **Namespace Whitelisting**: Only weaves methods within the provided list of namespaces.
* **Robust Exclusion**: Skips methods and types that are problematic for IL weaving, ensuring stability.
* **Comprehensive Logging**: A log file is produced detailing each action taken during weaving per type/method,
                             dumping the respective IL code for intricate debugging/validation 

## How it Works

`ProfilerWeaver` uses the [Mono.Cecil](https://github.com/jbevain/cecil) library to read, modify, and write 
.NET assemblies. It identifies methods within the whitelisted namespaces and injects calls to user-defined 
`BeginSample` and `EndSample` methods at the entry and exit points of these methods, respectively. This allows for 
runtime profiling without manual code modification.

# Usage
To run `ProfilerWeaver.exe`, execute it from the command line with the following arguments:

```bash
ProfilerWeaver.exe <assembly_path> <output_path> <manager_class_name> <whitelist_namespaces> <begin_method_name> <end_method_name>
```

**Arguments:**

*   `<assembly_path>`: The full path to the assembly (.dll) you want to weave.
*   `<output_path>`: The full path where the modified assembly should be saved.
*   `<manager_class_name>`: The fully qualified name of the class containing the `Begin` and `End` methods (e.g., `MyNamespace.MyProfilerManager`).
*   `<whitelist_namespaces>`: A comma-separated list of namespaces to include for weaving. Methods outside these namespaces will be ignored.
*   `<begin_method_name>`: The name of the static method within `manager_class_name` to call at the beginning of each woven method (e.g., `BeginSample`).
*   `<end_method_name>`: The name of the static method within `manager_class_name` to call at the end of each woven method (e.g., `EndSample`).

**Example:**

```bash
ProfilerWeaver.exe "C:\Projects\MyLib\bin\Debug\MyLib.dll" "C:\Projects\MyLib\bin\Debug\MyLib.Weaved.dll" "MyLib.Profiling.Profiler" "MyLib.One,MyLib.Two" "BeginSample" "EndSample"
```

# Limitations
Weaving IL can be very delicate when performed on certain methods/types, such as compiler-generated state machines,
existing try/catch/finally blocks, etc.

To improve reliability and reduce complexity, these methods/types are excluded from weaving.

**Exclusion Conditions:**

***Types***
* Type has the `[BurstCompile]` attribute (Unity Engine specific)

***Methods***
* Body is null/empty
* Method is a constructor
* Method is P/Invoke (e.g. `[LibraryImport(...)]`)
* Method is abstract (implementations are included)
* Method is asynchronous (e.g. `[AsyncStateMachineAttribute]`, `async`)
* Method is an iterator (e.g. `IteratorStateMachineAttribute`, `IEnumerator`)
* Method already contains exception handlers (compiler-generated or explicit):
  * **Keywords that may generate try/catch**
    * `try` / `catch` / `finally`
    * `using`
    * `lock`
    * `foreach`