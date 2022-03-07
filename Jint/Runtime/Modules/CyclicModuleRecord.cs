﻿using System;
using Esprima.Ast;
using System.Collections.Generic;
using Esprima;
using Jint.Native;
using Jint.Native.Object;
using Jint.Native.Promise;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Environments;
using Jint.Runtime.Interop;
using Jint.Runtime.Interpreter;

namespace Jint.Runtime.Modules;

#pragma warning disable CS0649 // never assigned to, waiting for new functionalities in spec

internal sealed record ResolvedBinding(CyclicModuleRecord Module, string BindingName)
{
    internal static ResolvedBinding Ambiguous => new(null, "ambiguous");
}

internal sealed record ImportEntry(
    string ModuleRequest,
    string ImportName,
    string LocalName
);

internal sealed record ExportEntry(
    string ExportName,
    string ModuleRequest,
    string ImportName,
    string LocalName
);

/// <summary>
/// https://tc39.es/ecma262/#sec-cyclic-module-records
/// https://tc39.es/ecma262/#sec-source-text-module-records
/// </summary>
public sealed class CyclicModuleRecord : ModuleRecord
{
    private Completion? _evalError;
    private int _dfsIndex;
    private int _dfsAncestorIndex;
    private readonly HashSet<string> _requestedModules;
    private CyclicModuleRecord _cycleRoot;
    private bool _hasTLA;
    private bool _asyncEvaluation;
    private PromiseCapability _topLevelCapability;
    private List<CyclicModuleRecord> _asyncParentModules;
    private int _asyncEvalOrder;
    private int _pendingAsyncDependencies;

    private readonly Module _source;
    private ExecutionContext _context;
    private readonly ObjectInstance _importMeta;
    private readonly List<ImportEntry> _importEntries;
    private readonly List<ExportEntry> _localExportEntries;
    private readonly List<ExportEntry> _indirectExportEntries;
    private readonly List<ExportEntry> _starExportEntries;
    private readonly List<KeyValuePair<string, JsValue>> _exportBuilderDeclarations = new();
    internal JsValue _evalResult;

    internal CyclicModuleRecord(Engine engine, Realm realm, Module source, string location, bool async) : base(engine, realm, location)
    {
        _source = source;

        _importMeta = _realm.Intrinsics.Object.Construct(1);
        _importMeta.DefineOwnProperty("url", new PropertyDescriptor(location, PropertyFlag.ConfigurableEnumerableWritable));

        HoistingScope.GetImportsAndExports(
            _source,
            out _requestedModules,
            out _importEntries,
            out _localExportEntries,
            out _indirectExportEntries,
            out _starExportEntries);

        //ToDo async modules
    }

    internal ModuleStatus Status { get; private set; }

    internal void BindExportedValue(string name, JsValue value)
    {
        if(_environment != null) ExceptionHelper.ThrowInvalidOperationException("Cannot bind exported values after the environment has been initialized");
        _exportBuilderDeclarations.Add(new KeyValuePair<string, JsValue>(name, value));
    }

    /// <summary>
    /// https://tc39.es/ecma262/#sec-getexportednames
    /// </summary>
    public override List<string> GetExportedNames(List<CyclicModuleRecord> exportStarSet = null)
    {
        exportStarSet ??= new List<CyclicModuleRecord>();
        if (exportStarSet.Contains(this))
        {
            //Reached the starting point of an export * circularity
            return new List<string>();
        }

        exportStarSet.Add(this);
        var exportedNames = new List<string>();
        for (var i = 0; i < _localExportEntries.Count; i++)
        {
            var e = _localExportEntries[i];
            exportedNames.Add(e.ImportName ?? e.ExportName);
        }

        for (var i = 0; i < _indirectExportEntries.Count; i++)
        {
            var e = _indirectExportEntries[i];
            exportedNames.Add(e.ImportName ?? e.ExportName);
        }

        for (var i = 0; i < _starExportEntries.Count; i++)
        {
            var e = _starExportEntries[i];
            var requestedModule = _engine._host.ResolveImportedModule(this, e.ModuleRequest);
            var starNames = requestedModule.GetExportedNames(exportStarSet);

            for (var j = 0; j < starNames.Count; j++)
            {
                var n = starNames[j];
                if (!"default".Equals(n) && !exportedNames.Contains(n))
                {
                    exportedNames.Add(n);
                }
            }
        }

        return exportedNames;
    }

    /// <summary>
    /// https://tc39.es/ecma262/#sec-resolveexport
    /// </summary>
    internal override ResolvedBinding ResolveExport(string exportName, List<ExportResolveSetItem> resolveSet = null)
    {
        resolveSet ??= new List<ExportResolveSetItem>();

        for (var i = 0; i < resolveSet.Count; i++)
        {
            var r = resolveSet[i];
            if (ReferenceEquals(this, r.Module) && exportName == r.ExportName)
            {
                // circular import request
                return null;
            }
        }

        resolveSet.Add(new ExportResolveSetItem(this, exportName));
        for (var i = 0; i < _localExportEntries.Count; i++)
        {
            var e = _localExportEntries[i];
            if (exportName == (e.ImportName ?? e.ExportName))
            {
                return new ResolvedBinding(this, e.LocalName ?? e.ExportName);
            }
        }

        for (var i = 0; i < _indirectExportEntries.Count; i++)
        {
            var e = _indirectExportEntries[i];
            if (exportName == (e.ImportName ?? e.ExportName))
            {
                var importedModule = _engine._host.ResolveImportedModule(this, e.ModuleRequest);
                if (e.ImportName == "*")
                {
                    return new ResolvedBinding(importedModule, "*namespace*");
                }
                else
                {
                    return importedModule.ResolveExport(e.ExportName, resolveSet);
                }
            }
        }

        if ("default".Equals(exportName))
        {
            return null;
        }

        ResolvedBinding starResolution = null;

        for (var i = 0; i < _starExportEntries.Count; i++)
        {
            var e = _starExportEntries[i];
            var importedModule = _engine._host.ResolveImportedModule(this, e.ModuleRequest);
            var resolution = importedModule.ResolveExport(exportName, resolveSet);
            if (resolution == ResolvedBinding.Ambiguous)
            {
                return resolution;
            }

            if (resolution is not null)
            {
                if (starResolution is null)
                {
                    starResolution = resolution;
                }
                else
                {
                    if (resolution.Module != starResolution.Module || resolution.BindingName != starResolution.BindingName)
                    {
                        return ResolvedBinding.Ambiguous;
                    }
                }
            }
        }

        return starResolution;
    }

    /// <summary>
    /// https://tc39.es/ecma262/#sec-moduledeclarationlinking
    /// </summary>
    public override void Link()
    {
        if (Status == ModuleStatus.Linking || Status == ModuleStatus.Evaluating)
        {
            ExceptionHelper.ThrowInvalidOperationException("Error while linking module: Module is already either linking or evaluating");
        }

        var stack = new Stack<CyclicModuleRecord>();

        try
        {
            InnerModuleLinking(this, stack, 0);
        }
        catch
        {
            foreach (var m in stack)
            {
                m.Status = ModuleStatus.Unlinked;
                m._environment = null;
                m._dfsIndex = -1;
                m._dfsAncestorIndex = -1;
            }

            Status = ModuleStatus.Unlinked;
            throw;
        }

        if (Status != ModuleStatus.Linked && Status != ModuleStatus.Unlinked)
        {
            ExceptionHelper.ThrowInvalidOperationException("Error while linking module: Module is neither linked or unlinked");
        }

        if (stack.Count > 0)
        {
            ExceptionHelper.ThrowInvalidOperationException("Error while linking module: One or more modules were not linked");
        }
    }

    /// <summary>
    /// https://tc39.es/ecma262/#sec-moduleevaluation
    /// </summary>
    public override JsValue Evaluate()
    {
        var module = this;

        if (module.Status != ModuleStatus.Linked &&
            module.Status != ModuleStatus.EvaluatingAsync &&
            module.Status != ModuleStatus.Evaluated)
        {
            ExceptionHelper.ThrowInvalidOperationException("Error while evaluating module: Module is in an invalid state");
        }

        if (module.Status == ModuleStatus.EvaluatingAsync || module.Status == ModuleStatus.Evaluated)
        {
            module = module._cycleRoot;
        }

        if (module._topLevelCapability is not null)
        {
            return module._topLevelCapability.PromiseInstance;
        }

        var stack = new Stack<CyclicModuleRecord>();
        var capability = PromiseConstructor.NewPromiseCapability(_engine, _realm.Intrinsics.Promise);
        var asyncEvalOrder = 0;
        module._topLevelCapability = capability;

        var result = Evaluate(module, stack, 0, ref asyncEvalOrder);

        if (result.Type != CompletionType.Normal)
        {
            foreach (var m in stack)
            {
                m.Status = ModuleStatus.Evaluated;
                m._evalError = result;
            }

            capability.Reject.Call(Undefined, new[] { result.Value });
        }
        else
        {
            if (module.Status != ModuleStatus.EvaluatingAsync && module.Status != ModuleStatus.Evaluated)
            {
                ExceptionHelper.ThrowInvalidOperationException("Error while evaluating module: Module is in an invalid state");
            }

            if (module._evalError is not null)
            {
                ExceptionHelper.ThrowInvalidOperationException("Error while evaluating module: Module is in an invalid state");
            }

            if (!module._asyncEvaluation)
            {
                if (module.Status != ModuleStatus.Evaluated)
                {
                    ExceptionHelper.ThrowInvalidOperationException("Error while evaluating module: Module is in an invalid state");
                }

                capability.Resolve.Call(Undefined, Array.Empty<JsValue>());
            }

            if (stack.Count > 0)
            {
                ExceptionHelper.ThrowInvalidOperationException("Error while evaluating module: Module is in an invalid state");
            }
        }

        return capability.PromiseInstance;
    }

    /// <summary>
    /// https://tc39.es/ecma262/#sec-InnerModuleLinking
    /// </summary>
    private int InnerModuleLinking(CyclicModuleRecord module, Stack<CyclicModuleRecord> stack, int index)
    {
        if (module.Status is
            ModuleStatus.Linking or
            ModuleStatus.Linked or
            ModuleStatus.EvaluatingAsync or
            ModuleStatus.Evaluating)
        {
            return index;
        }

        if (module.Status != ModuleStatus.Unlinked)
        {
            ExceptionHelper.ThrowInvalidOperationException("Error while linking module: Module in an invalid state");
        }

        module.Status = ModuleStatus.Linking;
        module._dfsIndex = index;
        module._dfsAncestorIndex = index;
        index++;
        stack.Push(module);

        var requestedModules = module._requestedModules;

        foreach (var moduleSpecifier in requestedModules)
        {
            var requiredModule = _engine._host.ResolveImportedModule(module, moduleSpecifier);

            //TODO: Should we link only when a module is requested? https://tc39.es/ecma262/#sec-example-cyclic-module-record-graphs Should we support retry?
            if (requiredModule.Status == ModuleStatus.Unlinked)
            {
                index = requiredModule.InnerModuleLinking(requiredModule, stack, index);
            }

            if (requiredModule.Status != ModuleStatus.Linking &&
                requiredModule.Status != ModuleStatus.Linked &&
                requiredModule.Status != ModuleStatus.Evaluated)
            {
                ExceptionHelper.ThrowInvalidOperationException($"Error while linking module: Required module is in an invalid state: {requiredModule.Status}");
            }

            if (requiredModule.Status == ModuleStatus.Linking && !stack.Contains(requiredModule))
            {
                ExceptionHelper.ThrowInvalidOperationException($"Error while linking module: Required module is in an invalid state: {requiredModule.Status}");
            }

            if (requiredModule.Status == ModuleStatus.Linking)
            {
                module._dfsAncestorIndex = Math.Min(module._dfsAncestorIndex, requiredModule._dfsAncestorIndex);
            }
        }

        module.InitializeEnvironment();

        if (StackReferenceCount(stack, module) != 1)
        {
            ExceptionHelper.ThrowInvalidOperationException("Error while linking module: Recursive dependency detected");
        }

        if (module._dfsIndex > module._dfsAncestorIndex)
        {
            ExceptionHelper.ThrowInvalidOperationException("Error while linking module: Recursive dependency detected");
        }

        if (module._dfsIndex == module._dfsAncestorIndex)
        {
            while (true)
            {
                var requiredModule = stack.Pop();
                requiredModule.Status = ModuleStatus.Linked;
                if (requiredModule == module)
                {
                    break;
                }
            }
        }

        return index;
    }

    /// <summary>
    /// https://tc39.es/ecma262/#sec-innermoduleevaluation
    /// </summary>
    private Completion Evaluate(CyclicModuleRecord module, Stack<CyclicModuleRecord> stack, int index, ref int asyncEvalOrder)
    {
        if (module.Status == ModuleStatus.EvaluatingAsync || module.Status == ModuleStatus.Evaluated)
        {
            if (module._evalError is null)
            {
                return new Completion(CompletionType.Normal, index, null, default);
            }

            return module._evalError.Value;
        }

        if (module.Status == ModuleStatus.Evaluating)
        {
            return new Completion(CompletionType.Normal, index, null, default);
        }

        if (module.Status != ModuleStatus.Linked)
        {
            ExceptionHelper.ThrowInvalidOperationException("Error while evaluating module: Module is in an invalid state");
        }


        module.Status = ModuleStatus.Evaluating;
        module._dfsIndex = index;
        module._dfsAncestorIndex = index;
        module._pendingAsyncDependencies = 0;
        index++;
        stack.Push(module);

        var requestedModules = module._requestedModules;

        foreach (var moduleSpecifier in requestedModules)
        {
            var requiredModule = _engine._host.ResolveImportedModule(module, moduleSpecifier);
            var result = Evaluate(module, stack, index, ref asyncEvalOrder);
            if (result.Type != CompletionType.Normal)
            {
                return result;
            }

            index = TypeConverter.ToInt32(result.Value);

            // TODO: Validate this behavior: https://tc39.es/ecma262/#sec-example-cyclic-module-record-graphs
            if (requiredModule.Status == ModuleStatus.Linked)
            {
                var evaluationResult = requiredModule.Evaluate();
                if (evaluationResult == null)
                {
                    ExceptionHelper.ThrowInvalidOperationException($"Error while evaluating module: Module evaluation did not return a promise");
                }
                else if (evaluationResult is not PromiseInstance promise)
                {
                    ExceptionHelper.ThrowInvalidOperationException($"Error while evaluating module: Module evaluation did not return a promise: {evaluationResult.Type}");
                }
                else if (promise.State == PromiseState.Rejected)
                {
                    ExceptionHelper.ThrowJavaScriptException(_engine, promise.Value, new Completion(CompletionType.Throw, promise.Value, null, new Location(new Position(), new Position(), moduleSpecifier)));
                }
                else if (promise.State != PromiseState.Fulfilled)
                {
                    ExceptionHelper.ThrowInvalidOperationException($"Error while evaluating module: Module evaluation did not return a fulfilled promise: {promise.State}");
                }
            }

            if (requiredModule.Status != ModuleStatus.Evaluating &&
                requiredModule.Status != ModuleStatus.EvaluatingAsync &&
                requiredModule.Status != ModuleStatus.Evaluated)
            {
                ExceptionHelper.ThrowInvalidOperationException($"Error while evaluating module: Module is in an invalid state: {requiredModule.Status}");
            }

            if (requiredModule.Status == ModuleStatus.Evaluating && !stack.Contains(requiredModule))
            {
                ExceptionHelper.ThrowInvalidOperationException($"Error while evaluating module: Module is in an invalid state: {requiredModule.Status}");
            }

            if (requiredModule.Status == ModuleStatus.Evaluating)
            {
                module._dfsAncestorIndex = Math.Min(module._dfsAncestorIndex, requiredModule._dfsAncestorIndex);
            }
            else
            {
                requiredModule = requiredModule._cycleRoot;
                if (requiredModule.Status != ModuleStatus.EvaluatingAsync && requiredModule.Status != ModuleStatus.Evaluated)
                {
                    ExceptionHelper.ThrowInvalidOperationException("Error while evaluating module: Module is in an invalid state");
                }
            }

            if (requiredModule._asyncEvaluation)
            {
                module._pendingAsyncDependencies++;
                requiredModule._asyncParentModules.Add(module);
            }
        }

        Completion completion;

        if (module._pendingAsyncDependencies > 0 || module._hasTLA)
        {
            if (module._asyncEvaluation)
            {
                ExceptionHelper.ThrowInvalidOperationException("Error while evaluating module: Module is in an invalid state");
            }

            module._asyncEvaluation = true;
            module._asyncEvalOrder = asyncEvalOrder++;
            if (module._pendingAsyncDependencies == 0)
            {
                completion = module.ExecuteAsync();
            }
            else
            {
                completion = module.Execute();
            }
        }
        else
        {
            completion = module.Execute();
        }

        if (StackReferenceCount(stack, module) != 1)
        {
            ExceptionHelper.ThrowInvalidOperationException("Error while evaluating module: Module is in an invalid state");
        }

        if (module._dfsAncestorIndex > module._dfsIndex)
        {
            ExceptionHelper.ThrowInvalidOperationException("Error while evaluating module: Module is in an invalid state");
        }

        if (module._dfsIndex == module._dfsAncestorIndex)
        {
            var done = false;
            while (!done)
            {
                var requiredModule = stack.Pop();
                if (!requiredModule._asyncEvaluation)
                {
                    requiredModule.Status = ModuleStatus.Evaluated;
                }
                else
                {
                    requiredModule.Status = ModuleStatus.EvaluatingAsync;
                }

                done = requiredModule == module;
                requiredModule._cycleRoot = module;
            }
        }

        return completion;
    }

    private static int StackReferenceCount(Stack<CyclicModuleRecord> stack, CyclicModuleRecord module)
    {
        var count = 0;
        foreach (var item in stack)
        {
            if (ReferenceEquals(item, module))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// https://tc39.es/ecma262/#sec-source-text-module-record-initialize-environment
    /// </summary>
    private void InitializeEnvironment()
    {
        for (var i = 0; i < _indirectExportEntries.Count; i++)
        {
            var e = _indirectExportEntries[i];
            var resolution = ResolveExport(e.ImportName ?? e.ExportName);
            if (resolution is null || resolution == ResolvedBinding.Ambiguous)
            {
                ExceptionHelper.ThrowSyntaxError(_realm, "Ambiguous import statement for identifier: " + e.ExportName);
            }
        }

        var realm = _realm;
        var env = JintEnvironment.NewModuleEnvironment(_engine, realm.GlobalEnv);
        _environment = env;

        if (_importEntries != null)
        {
            for (var i = 0; i < _importEntries.Count; i++)
            {
                var ie = _importEntries[i];
                var importedModule = _engine._host.ResolveImportedModule(this, ie.ModuleRequest);
                if (ie.ImportName == "*")
                {
                    var ns = GetModuleNamespace(importedModule);
                    env.CreateImmutableBinding(ie.LocalName, true);
                    env.InitializeBinding(ie.LocalName, ns);
                }
                else
                {
                    var resolution = importedModule.ResolveExport(ie.ImportName);
                    if (resolution is null || resolution == ResolvedBinding.Ambiguous)
                    {
                        ExceptionHelper.ThrowSyntaxError(_realm, "Ambiguous import statement for identifier " + ie.ImportName);
                    }

                    if (resolution.BindingName == "*namespace*")
                    {
                        var ns = GetModuleNamespace(resolution.Module);
                        env.CreateImmutableBinding(ie.LocalName, true);
                        env.InitializeBinding(ie.LocalName, ns);
                    }
                    else
                    {
                        env.CreateImportBinding(ie.LocalName, resolution.Module, resolution.BindingName);
                    }
                }
            }
        }

        var moduleContext = new ExecutionContext(this, _environment, _environment, null, realm, null);
        _context = moduleContext;

        _engine.EnterExecutionContext(_context);

        var hoistingScope = HoistingScope.GetModuleLevelDeclarations(_source);

        var varDeclarations = hoistingScope._variablesDeclarations;
        var declaredVarNames = new HashSet<string>();
        if (varDeclarations != null)
        {
            var boundNames = new List<string>();
            for (var i = 0; i < varDeclarations.Count; i++)
            {
                var d = varDeclarations[i];
                boundNames.Clear();
                d.GetBoundNames(boundNames);
                for (var j = 0; j < boundNames.Count; j++)
                {
                    var dn = boundNames[j];
                    if (declaredVarNames.Add(dn))
                    {
                        env.CreateMutableBinding(dn, false);
                        env.InitializeBinding(dn, Undefined);
                    }
                }
            }
        }

        var lexDeclarations = hoistingScope._lexicalDeclarations;

        if (lexDeclarations != null)
        {
            var boundNames = new List<string>();
            for (var i = 0; i < lexDeclarations.Count; i++)
            {
                var d = lexDeclarations[i];
                boundNames.Clear();
                d.GetBoundNames(boundNames);
                for (var j = 0; j < boundNames.Count; j++)
                {
                    var dn = boundNames[j];
                    if (d.IsConstantDeclaration())
                    {
                        env.CreateImmutableBinding(dn, true);
                    }
                    else
                    {
                        env.CreateMutableBinding(dn, false);
                    }
                }
            }
        }

        var functionDeclarations = hoistingScope._functionDeclarations;

        if (functionDeclarations != null)
        {
            for (var i = 0; i < functionDeclarations.Count; i++)
            {
                var d = functionDeclarations[i];
                var fn = d.Id?.Name ?? "*default*";
                var fd = new JintFunctionDefinition(_engine, d);
                env.CreateMutableBinding(fn, true);
                var fo = realm.Intrinsics.Function.InstantiateFunctionObject(fd, env);
                env.InitializeBinding(fn, fo);
            }
        }

        if (_exportBuilderDeclarations != null)
        {
            for (var i = 0; i < _exportBuilderDeclarations.Count; i++)
            {
                var d = _exportBuilderDeclarations[i];
                _environment.CreateImmutableBindingAndInitialize(d.Key, true, d.Value);
                _localExportEntries.Add(new ExportEntry(d.Key, null, null, null));
            }
            _exportBuilderDeclarations.Clear();
        }

        _engine.LeaveExecutionContext();
    }

    /// <summary>
    /// https://tc39.es/ecma262/#sec-source-text-module-record-execute-module
    /// </summary>
    private Completion Execute(PromiseCapability capability = null)
    {
        var moduleContext = new ExecutionContext(this, _environment, _environment, null, _realm);
        if (!_hasTLA)
        {
            using (new StrictModeScope(true, force: true))
            {
                _engine.EnterExecutionContext(moduleContext);
                var statementList = new JintStatementList(null, _source.Body);
                var result = statementList.Execute(_engine._activeEvaluationContext ?? new EvaluationContext(_engine)); //Create new evaluation context when called from e.g. module tests
                _engine.LeaveExecutionContext();
                return result;
            }
        }
        else
        {
            //ToDo async modules
            return default;
        }
    }

    /// <summary>
    /// https://tc39.es/ecma262/#sec-execute-async-module
    /// </summary>
    private Completion ExecuteAsync()
    {
        if (Status != ModuleStatus.Evaluating && Status != ModuleStatus.EvaluatingAsync || !_hasTLA)
        {
            ExceptionHelper.ThrowInvalidOperationException("Error while evaluating module: Module is in an invalid state");
        }

        var capability = PromiseConstructor.NewPromiseCapability(_engine, _realm.Intrinsics.Promise);

        var onFullfilled = new ClrFunctionInstance(_engine, "fulfilled", AsyncModuleExecutionFulfilled, 1, PropertyFlag.Configurable);
        var onRejected = new ClrFunctionInstance(_engine, "rejected", AsyncModuleExecutionRejected, 1, PropertyFlag.Configurable);

        PromiseOperations.PerformPromiseThen(_engine, (PromiseInstance) capability.PromiseInstance, onFullfilled, onRejected, null);

        return Execute(capability);
    }

    /// <summary>
    /// https://tc39.es/ecma262/#sec-gather-available-ancestors
    /// </summary>
    private void GatherAvailableAncestors(List<CyclicModuleRecord> execList)
    {
        foreach (var m in _asyncParentModules)
        {
            if (!execList.Contains(m) && m._cycleRoot._evalError is null)
            {
                if (m.Status != ModuleStatus.EvaluatingAsync ||
                    m._evalError is not null ||
                    !m._asyncEvaluation ||
                    m._pendingAsyncDependencies <= 0)
                {
                    ExceptionHelper.ThrowInvalidOperationException("Error while evaluating module: Module is in an invalid state");
                }

                if (--m._pendingAsyncDependencies == 0)
                {
                    execList.Add(m);
                    if (!m._hasTLA)
                    {
                        m.GatherAvailableAncestors(execList);
                    }
                }
            }
        }
    }

    /// <summary>
    /// https://tc39.es/ecma262/#sec-async-module-execution-fulfilled
    /// </summary>
    private JsValue AsyncModuleExecutionFulfilled(JsValue thisObj, JsValue[] arguments)
    {
        var module = (CyclicModuleRecord) arguments.At(0);
        if (module.Status == ModuleStatus.Evaluated)
        {
            if (module._evalError is not null)
            {
                ExceptionHelper.ThrowInvalidOperationException("Error while evaluating module: Module is in an invalid state");
            }

            return Undefined;
        }

        if (module.Status != ModuleStatus.EvaluatingAsync ||
            !module._asyncEvaluation ||
            module._evalError is not null)
        {
            ExceptionHelper.ThrowInvalidOperationException("Error while evaluating module: Module is in an invalid state");
        }

        if (module._topLevelCapability is not null)
        {
            if (module._cycleRoot is null)
            {
                ExceptionHelper.ThrowInvalidOperationException("Error while evaluating module: Module is in an invalid state");
            }

            module._topLevelCapability.Resolve.Call(Undefined, Array.Empty<JsValue>());
        }

        var execList = new List<CyclicModuleRecord>();
        module.GatherAvailableAncestors(execList);
        execList.Sort((x, y) => x._asyncEvalOrder - y._asyncEvalOrder);

        for (var i = 0; i < execList.Count; i++)
        {
            var m = execList[i];
            if (m.Status == ModuleStatus.Evaluated && m._evalError is null)
            {
                ExceptionHelper.ThrowInvalidOperationException("Error while evaluating module: Module is in an invalid state");
            }
            else if (m._hasTLA)
            {
                m.ExecuteAsync();
            }
            else
            {
                var result = m.Execute();
                if (result.Type != CompletionType.Normal)
                {
                    AsyncModuleExecutionRejected(Undefined, new[] { m, result.Value });
                }
                else
                {
                    m.Status = ModuleStatus.Evaluated;
                    if (m._topLevelCapability is not null)
                    {
                        if (m._cycleRoot is null)
                        {
                            ExceptionHelper.ThrowInvalidOperationException("Error while evaluating module: Module is in an invalid state");
                        }

                        m._topLevelCapability.Resolve.Call(Undefined, Array.Empty<JsValue>());
                    }
                }
            }
        }

        return Undefined;
    }

    /// <summary>
    /// https://tc39.es/ecma262/#sec-async-module-execution-rejected
    /// </summary>
    private JsValue AsyncModuleExecutionRejected(JsValue thisObj, JsValue[] arguments)
    {
        var module = (CyclicModuleRecord) arguments.At(0);
        var error = arguments.At(1);

        if (module.Status == ModuleStatus.Evaluated)
        {
            if (module._evalError is null)
            {
                ExceptionHelper.ThrowInvalidOperationException("Error while evaluating module: Module is in an invalid state");
            }

            return Undefined;
        }

        if (module.Status != ModuleStatus.EvaluatingAsync ||
            !module._asyncEvaluation ||
            module._evalError is not null)
        {
            ExceptionHelper.ThrowInvalidOperationException("Error while evaluating module: Module is in an invalid state");
        }

        module._evalError = new Completion(CompletionType.Throw, error, null, default);
        module.Status = ModuleStatus.Evaluated;

        var asyncParentModules = module._asyncParentModules;
        for (var i = 0; i < asyncParentModules.Count; i++)
        {
            var m = asyncParentModules[i];
            AsyncModuleExecutionRejected(thisObj, new[] { m, error });
        }

        if (module._topLevelCapability is not null)
        {
            if (module._cycleRoot is null)
            {
                ExceptionHelper.ThrowInvalidOperationException("Error while evaluating module: Module is in an invalid state");
            }

            module._topLevelCapability.Reject.Call(Undefined, new[] { error });
        }


        return Undefined;
    }

    public override object ToObject()
    {
        ExceptionHelper.ThrowNotSupportedException();
        return null;
    }

    public override string ToString()
    {
        return $"{Type}: {Location}";
    }
}