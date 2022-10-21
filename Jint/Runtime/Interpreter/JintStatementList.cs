using Esprima.Ast;
using Jint.Runtime.Environments;
using Jint.Runtime.Interpreter.Statements;

namespace Jint.Runtime.Interpreter
{
    internal sealed class JintStatementList
    {
        internal sealed record FastResolvedJintStatement(
            JintStatement Statement,
            Completion? FastResolvedValue
        );
        
        private readonly Statement? _statement;
        private readonly NodeList<Statement> _statements;

        private FastResolvedJintStatement[]? _jintStatements;
        private bool _initialized;
        private readonly bool _generator;

        public JintStatementList(IFunction function)
            : this((BlockStatement) function.Body)
        {
            _generator = function.Generator;
        }

        public JintStatementList(BlockStatement blockStatement)
            : this(blockStatement, blockStatement.Body)
        {
        }

        public JintStatementList(Program program)
            : this(null, program.Body)
        {
        }

        public JintStatementList(Statement? statement, in NodeList<Statement> statements)
        {
            _statement = statement;
            _statements = statements;
        }

        private void Initialize(EvaluationContext context)
        {
            var jintStatements = new FastResolvedJintStatement[_statements.Count];
            for (var i = 0; i < jintStatements.Length; i++)
            {
                var esprimaStatement = _statements[i];
                var statement = JintStatement.Build(esprimaStatement);
                // When in debug mode, don't do FastResolve: Stepping requires each statement to be actually executed.
                var value = context.DebugMode ? null : JintStatement.FastResolve(esprimaStatement);
                jintStatements[i] = new FastResolvedJintStatement(
                    Statement: statement,
                    FastResolvedValue: value
                );
            }

            _jintStatements = jintStatements;
        }

        public IJintStatementListEnumerator GetEnumerator(EvaluationContext context)
        {
            if (!_initialized)
            {
                Initialize(context);
                _initialized = true;
            }

            if (_statement is not null)
            {
                context.LastSyntaxElement = _statement;
                context.RunBeforeExecuteStatementChecks(_statement);
            }

            return new JintStatementListEnumerator(_jintStatements!, context);
        }

        //TODO: This method should be removed once all callers have migrated
        public Completion Execute(EvaluationContext context)
        {
            var enumerator = GetEnumerator(context);

            while (enumerator.MoveNext())
            {
            }
            
            return enumerator.Current;
        }

        /// <summary>
        /// https://tc39.es/ecma262/#sec-blockdeclarationinstantiation
        /// </summary>
        internal static void BlockDeclarationInstantiation(
            Engine engine,
            EnvironmentRecord env,
            List<Declaration> declarations)
        {
            var privateEnv = env._engine.ExecutionContext.PrivateEnvironment;
            var boundNames = new List<string>();
            for (var i = 0; i < declarations.Count; i++)
            {
                var d = declarations[i];
                boundNames.Clear();
                d.GetBoundNames(boundNames);
                for (var j = 0; j < boundNames.Count; j++)
                {
                    var dn = boundNames[j];
                    if (d is VariableDeclaration { Kind: VariableDeclarationKind.Const })
                    {
                        env.CreateImmutableBinding(dn, strict: true);
                    }
                    else
                    {
                        env.CreateMutableBinding(dn, canBeDeleted: false);
                    }
                }

                if (d is FunctionDeclaration functionDeclaration)
                {
                    var definition = new JintFunctionDefinition(functionDeclaration);
                    var fn = definition.Name!;
                    var fo = env._engine.Realm.Intrinsics.Function.InstantiateFunctionObject(definition, env, privateEnv);
                    env.InitializeBinding(fn, fo);
                }
            }
        }
    }
}
