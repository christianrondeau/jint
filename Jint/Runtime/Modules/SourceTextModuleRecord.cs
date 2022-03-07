﻿using System.Collections.Generic;
using Esprima.Ast;
using Jint.Native.Object;
using Jint.Native.Promise;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Environments;
using Jint.Runtime.Interpreter;

namespace Jint.Runtime.Modules;

/// <summary>
/// https://tc39.es/ecma262/#importentry-record
/// </summary>
internal sealed record ImportEntry(
    string ModuleRequest,
    string ImportName,
    string LocalName
);

/// <summary>
/// https://tc39.es/ecma262/#exportentry-record
/// </summary>
internal sealed record ExportEntry(
    string ExportName,
    string ModuleRequest,
    string ImportName,
    string LocalName
);

/// <summary>
/// https://tc39.es/ecma262/#sec-source-text-module-records
/// </summary>
public class SourceTextModuleRecord : CyclicModuleRecord
{
    private readonly Module _source;
    private ExecutionContext _context;
    private readonly ObjectInstance _importMeta;
    private readonly List<ImportEntry> _importEntries;
    internal readonly List<ExportEntry> _localExportEntries;
    private readonly List<ExportEntry> _indirectExportEntries;
    private readonly List<ExportEntry> _starExportEntries;

    internal SourceTextModuleRecord(Engine engine, Realm realm, Module source, string location, bool async)
        : base(engine, realm, source, location, async)
    {
        _source = source;

        // https://tc39.es/ecma262/#sec-parsemodule
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
    /// https://tc39.es/ecma262/#sec-source-text-module-record-initialize-environment
    /// </summary>
    protected override void InitializeEnvironment()
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

        _engine.LeaveExecutionContext();
    }

    /// <summary>
    /// https://tc39.es/ecma262/#sec-source-text-module-record-execute-module
    /// </summary>
    internal override Completion ExecuteModule(PromiseCapability capability = null)
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
}