using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length == 2 && args[0] == "--list-hooks")
{
    using var source = AssemblyDefinition.ReadAssembly(args[1]);
    foreach (var candidate in source.MainModule.Types.SelectMany(Flatten).SelectMany(type => type.Methods).Where(method => method.HasBody))
    {
        var hooks = candidate.Body.Instructions
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>()
            .Where(reference => reference.DeclaringType.FullName.StartsWith("IL.", StringComparison.Ordinal) || reference.DeclaringType.FullName.StartsWith("On.", StringComparison.Ordinal))
            .Select(reference => $"{reference.DeclaringType.FullName}.{reference.Name}")
            .Distinct()
            .ToArray();
        if (hooks.Length > 0)
            Console.WriteLine($"{candidate.FullName}: {string.Join(", ", hooks)}");
    }
    return 0;
}

if (args.Length == 3 && args[0] == "--show-method")
{
    using var source = AssemblyDefinition.ReadAssembly(args[1]);
    var found = false;
    foreach (var method in source.MainModule.Types.SelectMany(Flatten).SelectMany(type => type.Methods).Where(method => method.FullName.Contains(args[2], StringComparison.Ordinal)))
    {
        found = true;
        Console.WriteLine(method.FullName);
        if (!method.HasBody) continue;
        foreach (var instruction in method.Body.Instructions)
            Console.WriteLine($"  {instruction.Offset:X4}: {instruction.OpCode} {instruction.Operand}");
    }
    if (!found) throw new InvalidOperationException($"No method contains '{args[2]}'.");
    return 0;
}

if (args.Length == 3 && args[0] == "--find-ref")
{
    using var source = AssemblyDefinition.ReadAssembly(args[1]);
    foreach (var method in source.MainModule.Types.SelectMany(Flatten).SelectMany(type => type.Methods).Where(method => method.HasBody))
    {
        var references = method.Body.Instructions
            .Where(instruction => instruction.Operand is MemberReference reference && reference.FullName.Contains(args[2], StringComparison.Ordinal))
            .ToArray();
        if (references.Length == 0) continue;
        Console.WriteLine(method.FullName);
        foreach (var instruction in references)
            Console.WriteLine($"  {instruction.Offset:X4}: {instruction.OpCode} {instruction.Operand}");
    }
    return 0;
}

if (args.Length == 3 && args[0] == "--show-type")
{
    using var source = AssemblyDefinition.ReadAssembly(args[1]);
    var type = source.MainModule.GetType(args[2]) ?? throw new InvalidOperationException($"Type '{args[2]}' was not found.");
    Console.WriteLine($"TYPE {type.FullName}; BASE {type.BaseType}");
    foreach (var field in type.Fields) Console.WriteLine($"FIELD {field.FieldType} {field.Name}");
    foreach (var method in type.Methods) Console.WriteLine($"METHOD {method.FullName}");
    return 0;
}

if (args.Length == 4 && args[0] == "--patch-spring")
{
    using var spring = AssemblyDefinition.ReadAssembly(args[1]);
    using var runtime = AssemblyDefinition.ReadAssembly(args[2]);
    var springModule = spring.MainModule;
    var configType = runtime.MainModule.GetType("MonoMod.RuntimeDetour.DetourConfig")
        ?? throw new InvalidOperationException("Current DetourConfig type was not found.");
    var configContextType = runtime.MainModule.GetType("MonoMod.RuntimeDetour.DetourConfigContext")
        ?? throw new InvalidOperationException("Current DetourConfigContext type was not found.");
    var detourContextType = runtime.MainModule.GetType("MonoMod.RuntimeDetour.DetourContext")
        ?? throw new InvalidOperationException("Current DetourContext type was not found.");
    var configConstructor = configType.Methods.Single(method => method.IsConstructor && method.Parameters.Count == 4);
    var contextConstructor = configContextType.Methods.Single(method => method.IsConstructor && method.Parameters.Count == 1);
    var useContext = detourContextType.Methods.Single(method => method.Name == "Use" && method.Parameters.Count == 0);

    var targets = new[]
    {
        ("Celeste.Mod.SpringCollab2020.Entities.BubbleReturnBerry", "Load"),
        ("Celeste.Mod.SpringCollab2020.Entities.UpsideDownJumpThru", "activateHooks")
    };
    foreach (var (typeName, methodName) in targets)
    {
        var type = springModule.GetType(typeName)
            ?? throw new InvalidOperationException($"Spring Collab compatibility target was not found: {typeName}");
        var method = type.Methods.Single(candidate => candidate.Name == methodName && candidate.HasBody);
        PatchLegacyDetourContext(spring, method, configConstructor, contextConstructor, useContext);
    }

    spring.Write(args[3]);
    Console.WriteLine($"Replaced obsolete Spring Collab hook-order contexts with current DetourConfigContext scopes. Output {args[3]}");
    return 0;
}

if (args.Length == 3 && args[0] == "--disable-mhh-hooks")
{
    using var compatAssembly = AssemblyDefinition.ReadAssembly(args[1]);
    var compatModuleType = compatAssembly.MainModule.GetType("Celeste.Mod.MaxHelpingHand.Module.MaxHelpingHandModule")
        ?? throw new InvalidOperationException("MaxHelpingHandModule was not found.");
    var compatHookMods = compatModuleType.Methods.SingleOrDefault(method => method.Name == "HookMods" && method.Parameters.Count == 0 && method.HasBody)
        ?? throw new InvalidOperationException("MaxHelpingHandModule.HookMods() was not found.");

    // Webleste's newer MonoMod runtime cannot safely apply several MHH IL hooks.
    // Skip only runtime hook registration; keep module loading, map entities,
    // triggers, assets, and other content initialization intact.
    compatHookMods.Body.ExceptionHandlers.Clear();
    compatHookMods.Body.Instructions.Clear();
    compatHookMods.Body.Variables.Clear();
    compatHookMods.Body.InitLocals = false;
    compatHookMods.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    compatAssembly.Write(args[2]);
    Console.WriteLine($"Disabled MaxHelpingHand runtime hook registration in {compatHookMods.FullName}. Output {args[2]}");
    return 0;
}

if (args.Length == 3 && args[0] == "--skip-incompatible-il-hooks")
{
    using var compatAssembly = AssemblyDefinition.ReadAssembly(args[1]);
    var compatModuleType = compatAssembly.MainModule.GetType("Celeste.Mod.MaxHelpingHand.Module.MaxHelpingHandModule")
        ?? throw new InvalidOperationException("MaxHelpingHandModule was not found.");
    var compatHookMods = compatModuleType.Methods.SingleOrDefault(method => method.Name == "HookMods" && method.Parameters.Count == 0 && method.HasBody)
        ?? throw new InvalidOperationException("MaxHelpingHandModule.HookMods() was not found.");
    compatHookMods.Body.ExceptionHandlers.Clear();
    compatHookMods.Body.Instructions.Clear();
    compatHookMods.Body.Variables.Clear();
    compatHookMods.Body.InitLocals = false;
    compatHookMods.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

    var ilEventCalls = 0;
    var ilHookConstructors = 0;
    var ilHookDisposals = 0;
    foreach (var method in compatAssembly.MainModule.Types.SelectMany(Flatten).SelectMany(type => type.Methods).Where(method => method.HasBody))
    {
        var processor = method.Body.GetILProcessor();
        foreach (var instruction in method.Body.Instructions.ToArray())
        {
            if (instruction.Operand is not MethodReference reference) continue;

            if ((instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
                && reference.DeclaringType.FullName.StartsWith("IL.", StringComparison.Ordinal)
                && (reference.Name.StartsWith("add_", StringComparison.Ordinal) || reference.Name.StartsWith("remove_", StringComparison.Ordinal)))
            {
                // The event accessor consumes the delegate on the evaluation stack.
                // Pop it instead of registering an IL manipulator incompatible with Webleste's MonoMod runtime.
                instruction.OpCode = OpCodes.Pop;
                instruction.Operand = null;
                ilEventCalls++;
            }
            else if (instruction.OpCode == OpCodes.Newobj
                && reference.DeclaringType.FullName == "MonoMod.RuntimeDetour.ILHook"
                && reference.Name == ".ctor")
            {
                // Manual ILHook sites push a MethodBase and manipulator; consume both
                // and leave null for the following field/local store.
                if (reference.Parameters.Count != 2)
                    throw new InvalidOperationException($"Unexpected ILHook constructor in {method.FullName}: {reference.FullName}");
                instruction.OpCode = OpCodes.Pop;
                instruction.Operand = null;
                var secondPop = processor.Create(OpCodes.Pop);
                var nullReference = processor.Create(OpCodes.Ldnull);
                processor.InsertAfter(instruction, secondPop);
                processor.InsertAfter(secondPop, nullReference);
                ilHookConstructors++;
            }
            else if ((instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
                && reference.DeclaringType.FullName == "MonoMod.RuntimeDetour.ILHook"
                && reference.Name == "Dispose"
                && reference.Parameters.Count == 0)
            {
                // Matching hook fields are null after construction was skipped.
                instruction.OpCode = OpCodes.Pop;
                instruction.Operand = null;
                ilHookDisposals++;
            }
        }
    }

    if (ilEventCalls == 0 && ilHookConstructors == 0)
        throw new InvalidOperationException("No MaxHelpingHand IL hook registrations were found.");
    compatAssembly.Write(args[2]);
    Console.WriteLine($"Skipped {ilEventCalls} IL hook event accessors, {ilHookConstructors} manual ILHook constructors, and {ilHookDisposals} ILHook disposals; disabled MaxHelpingHand HookMods(). Output {args[2]}");
    return 0;
}

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: PatchMaxHelpingHand <original.dll> <compat.dll> OR --patch-spring <original.dll> <MonoMod.RuntimeDetour.dll> <compat.dll> OR --disable-mhh-hooks <input.dll> <output.dll> OR --skip-incompatible-il-hooks <input.dll> <output.dll> OR --list-hooks <original.dll> OR --show-method <original.dll> <name-fragment> OR --find-ref <original.dll> <name-fragment> OR --show-type <assembly.dll> <type-name>");
    return 2;
}

using var assembly = AssemblyDefinition.ReadAssembly(args[0]);
var moduleType = assembly.MainModule.GetType("Celeste.Mod.MaxHelpingHand.Module.MaxHelpingHandModule");
var moduleLoad = moduleType?.Methods.SingleOrDefault(method => method.Name == "Load" && method.Parameters.Count == 0 && method.HasBody);
if (moduleLoad is null)
    throw new InvalidOperationException("MaxHelpingHandModule.Load() was not found.");

var moduleCalls = moduleLoad.Body.Instructions
    .Where(instruction => instruction.OpCode == OpCodes.Call && instruction.Operand is MethodReference reference
        && reference.Name == "Load" && reference.Parameters.Count == 0
        && reference.ReturnType.MetadataType == MetadataType.Void
        && reference.DeclaringType.FullName.StartsWith("Celeste.Mod.MaxHelpingHand.", StringComparison.Ordinal))
    .ToArray();
if (moduleCalls.Length == 0)
    throw new InvalidOperationException("No MaxHelpingHand module initialization calls were found.");

foreach (var call in moduleCalls)
    GuardCall(moduleLoad, call);

// MaxHelpingHand 1.40.10 was built for a newer Everest/MonoMod runtime. Its
// individual IL hooks can throw while Everest calls HookMods from LoadContent,
// which otherwise aborts Celeste's first frame. Isolate each MHH hook setup so
// a runtime-incompatible helper is skipped without stopping the remaining
// helpers or the game startup.
var hookMods = moduleType.Methods.SingleOrDefault(method => method.Name == "HookMods" && method.Parameters.Count == 0 && method.HasBody);
if (hookMods is null)
    throw new InvalidOperationException("MaxHelpingHandModule.HookMods() was not found.");

var hookCalls = hookMods.Body.Instructions
    .Where(instruction => instruction.OpCode == OpCodes.Call && instruction.Operand is MethodReference reference
        && reference.ReturnType.MetadataType == MetadataType.Void
        && reference.DeclaringType.FullName.StartsWith("Celeste.Mod.MaxHelpingHand.", StringComparison.Ordinal))
    .ToArray();
if (hookCalls.Length == 0)
    throw new InvalidOperationException("No MaxHelpingHand hook setup calls were found.");

foreach (var call in hookCalls)
    GuardCall(hookMods, call);

assembly.Write(args[1]);
Console.WriteLine($"Added exception isolation around {moduleCalls.Length} MaxHelpingHand helper initializers and {hookCalls.Length} hook setup calls. Output {args[1]}");
return 0;

void GuardCall(MethodDefinition method, Instruction call)
{
    var body = method.Body;
    var after = Instruction.Create(OpCodes.Nop);
    var normalExit = Instruction.Create(OpCodes.Leave, after);
    var catchStart = Instruction.Create(OpCodes.Pop);
    var catchExit = Instruction.Create(OpCodes.Leave, after);
    var processor = body.GetILProcessor();
    processor.InsertAfter(call, normalExit);
    processor.InsertAfter(normalExit, catchStart);
    processor.InsertAfter(catchStart, catchExit);
    processor.InsertAfter(catchExit, after);
    var exceptionType = new TypeReference("System", "Exception", assembly.MainModule, assembly.MainModule.TypeSystem.CoreLibrary, false);
    body.ExceptionHandlers.Insert(0, new ExceptionHandler(ExceptionHandlerType.Catch)
    {
        TryStart = call,
        TryEnd = catchStart,
        HandlerStart = catchStart,
        HandlerEnd = after,
        CatchType = exceptionType
    });
    body.MaxStackSize = Math.Max(body.MaxStackSize, 1);
}

void PatchLegacyDetourContext(
    AssemblyDefinition targetAssembly,
    MethodDefinition method,
    MethodDefinition configConstructor,
    MethodDefinition contextConstructor,
    MethodDefinition useContext)
{
    var module = targetAssembly.MainModule;
    var body = method.Body;
    var instructions = body.Instructions;
    var constructorIndex = instructions.ToList().FindIndex(instruction => instruction.OpCode == OpCodes.Newobj
        && instruction.Operand is MethodReference reference
        && reference.DeclaringType.FullName == "MonoMod.RuntimeDetour.DetourContext"
        && reference.Name == ".ctor");
    if (constructorIndex < 0)
        throw new InvalidOperationException($"No obsolete DetourContext constructor in {method.FullName}.");

    var oldStart = instructions[constructorIndex];
    var oldEndIndex = -1;
    for (var index = constructorIndex; index < Math.Min(constructorIndex + 8, instructions.Count); index++)
    {
        if (instructions[index].OpCode == OpCodes.Stloc_0
            || instructions[index].OpCode == OpCodes.Stloc
            || instructions[index].OpCode == OpCodes.Stloc_S)
        {
            oldEndIndex = index;
            break;
        }
    }
    if (oldEndIndex < 0 || !instructions.Skip(constructorIndex).Take(oldEndIndex - constructorIndex + 1)
            .Any(instruction => instruction.OpCode == OpCodes.Ldstr && Equals(instruction.Operand, "*")))
        throw new InvalidOperationException($"Unexpected DetourContext setup sequence in {method.FullName}.");

    var oldBlock = instructions.Skip(constructorIndex).Take(oldEndIndex - constructorIndex + 1).ToArray();
    var afterBlock = oldEndIndex + 1 < instructions.Count ? instructions[oldEndIndex + 1] : null;
    var store = oldBlock[^1];
    var contextLocal = store.Operand as VariableDefinition;
    if (contextLocal is null && store.OpCode == OpCodes.Stloc_0 && body.Variables.Count > 0)
        contextLocal = body.Variables[0];
    if (contextLocal is null)
        throw new InvalidOperationException($"Could not identify DetourContext local in {method.FullName}.");

    var localIndex = body.Variables.IndexOf(contextLocal);
    var oldContextLocal = contextLocal;
    var disposableType = module.ImportReference(typeof(IDisposable));
    oldContextLocal.VariableType = disposableType;
    var nullableInt = module.ImportReference(typeof(int?));
    var nullableLocal = new VariableDefinition(nullableInt);
    body.Variables.Add(nullableLocal);

    var configCtor = module.ImportReference(configConstructor);
    var configContextCtor = module.ImportReference(contextConstructor);
    var use = module.ImportReference(useContext);
    var dataScope = module.ImportReference(useContext.ReturnType);
    var first = Instruction.Create(OpCodes.Ldstr, "SpringCollab2020");
    var replacement = new[]
    {
        first,
        Instruction.Create(OpCodes.Ldloca, nullableLocal),
        Instruction.Create(OpCodes.Initobj, nullableInt),
        Instruction.Create(OpCodes.Ldloc, nullableLocal),
        Instruction.Create(OpCodes.Ldnull),
        Instruction.Create(OpCodes.Ldc_I4_1),
        Instruction.Create(OpCodes.Newarr, module.TypeSystem.String),
        Instruction.Create(OpCodes.Dup),
        Instruction.Create(OpCodes.Ldc_I4_0),
        Instruction.Create(OpCodes.Ldstr, "*"),
        Instruction.Create(OpCodes.Stelem_Ref),
        Instruction.Create(OpCodes.Newobj, configCtor),
        Instruction.Create(OpCodes.Newobj, configContextCtor),
        Instruction.Create(OpCodes.Call, use),
        Instruction.Create(OpCodes.Box, dataScope),
        Instruction.Create(OpCodes.Stloc, oldContextLocal)
    };

    foreach (var handler in body.ExceptionHandlers)
    {
        if (handler.TryStart == oldStart) handler.TryStart = first;
        if (handler.TryEnd != null && oldBlock.Contains(handler.TryEnd)) handler.TryEnd = afterBlock;
        if (handler.HandlerStart == oldStart) handler.HandlerStart = first;
        if (handler.HandlerEnd != null && oldBlock.Contains(handler.HandlerEnd)) handler.HandlerEnd = afterBlock;
        if (handler.FilterStart == oldStart) handler.FilterStart = first;
    }
    foreach (var instruction in instructions)
    {
        if (instruction.Operand is Instruction target && oldBlock.Contains(target))
        {
            if (target != oldStart)
                throw new InvalidOperationException($"Branch enters obsolete DetourContext setup in {method.FullName}.");
            instruction.Operand = first;
        }
        else if (instruction.Operand is Instruction[] targets && targets.Any(oldBlock.Contains))
        {
            throw new InvalidOperationException($"Switch enters obsolete DetourContext setup in {method.FullName}.");
        }
    }

    var processor = body.GetILProcessor();
    foreach (var instruction in oldBlock)
        processor.Remove(instruction);
    if (afterBlock is null)
    {
        foreach (var instruction in replacement)
            processor.Append(instruction);
    }
    else
    {
        processor.InsertBefore(afterBlock, first);
        for (var index = 1; index < replacement.Length; index++)
            processor.InsertAfter(replacement[index - 1], replacement[index]);
    }
    body.MaxStackSize = Math.Max(body.MaxStackSize, 6);
    Console.WriteLine($"Patched {method.FullName} (local {localIndex}).");
}

static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
{
    yield return type;
    foreach (var child in type.NestedTypes.SelectMany(Flatten))
        yield return child;
}
