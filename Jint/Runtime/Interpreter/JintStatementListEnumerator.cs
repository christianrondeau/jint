using Esprima.Ast;
using Jint.Native;
using Jint.Native.Error;
using Jint.Runtime.Interpreter.Statements;

namespace Jint.Runtime.Interpreter;

internal interface IJintStatementListEnumerator
{
    public Completion Current { get; }
    bool MoveNext();
}

internal sealed class JintStatementListEnumerator : IJintStatementListEnumerator
{
    private readonly JintStatementList.FastResolvedJintStatement[] _jintStatements;
    private readonly EvaluationContext _evaluationContext;

    private Completion _lastCompletion;
    private Completion _lastSuccessfulCompletion;
    // The value of a StatementList is the value of the last value-producing item in the StatementList
    private JsValue? _lastSuccessfulValue;
    private uint _index;

    public Completion Current
    {
        get
        {
            var value = _lastCompletion.Type != CompletionType.Normal
                ? _lastCompletion.Value ?? _lastSuccessfulCompletion.Value!
                : _lastSuccessfulValue ?? JsValue.Undefined;
            return new Completion(_lastCompletion.Type, value, _lastCompletion._source);
        }
    }

    public JintStatementListEnumerator(JintStatementList.FastResolvedJintStatement[] jintStatements, EvaluationContext evaluationContext)
    {
        _jintStatements = jintStatements;
        _evaluationContext = evaluationContext;
    }

    public bool MoveNext()
    {
        if (_index >= _jintStatements.Length)
            return false;


        var pair = _jintStatements[_index];
        _index++;

        var s = pair.Statement;
        _lastCompletion = pair.FastResolvedValue.GetValueOrDefault();
        if (_lastCompletion.Value is null)
        {
            try
            {
                _lastCompletion = s.Execute(_evaluationContext);
            }
            catch (Exception ex)
            {
                if (ex is JintException)
                {
                    _lastCompletion = HandleException(_evaluationContext, ex, s);
                    return false;
                }

                throw;
            }
        }

        if (_lastCompletion.Type != CompletionType.Normal)
        {
            return false;
        }

        _lastSuccessfulCompletion = _lastCompletion;
        if (_lastCompletion.Value is not null)
        {
            _lastSuccessfulValue = _lastCompletion.Value;
        }

        return true;
    }

    private static Completion HandleException(EvaluationContext context, Exception exception, JintStatement? s)
    {
        if (exception is JavaScriptException javaScriptException)
        {
            return CreateThrowCompletion(s, javaScriptException);
        }
        if (exception is TypeErrorException typeErrorException)
        {
            var node = typeErrorException.Node ?? s!._statement;
            return CreateThrowCompletion(context.Engine.Realm.Intrinsics.TypeError, typeErrorException, node);
        }
        if (exception is RangeErrorException rangeErrorException)
        {
            return CreateThrowCompletion(context.Engine.Realm.Intrinsics.RangeError, rangeErrorException, s!._statement);
        }

        // should not happen unless there's problem in the engine
        throw exception;
    }

    private static Completion CreateThrowCompletion(ErrorConstructor errorConstructor, Exception e, SyntaxElement s)
    {
        var error = errorConstructor.Construct(e.Message);
        return new Completion(CompletionType.Throw, error, s);
    }

    private static Completion CreateThrowCompletion(JintStatement? s, JavaScriptException v)
    {
        SyntaxElement source = s!._statement;
        if (v.Location != default)
        {
            source = EsprimaExtensions.CreateLocationNode(v.Location);
        }

        return new Completion(CompletionType.Throw, v.Error, source);
    }
}
