using Esprima.Ast;
using Jint.Native.Promise;

namespace Jint.Runtime.Interpreter.Expressions;

internal sealed class JintAwaitExpression : JintExpression
{
    private JintExpression _awaitExpression = null!;

    public JintAwaitExpression(AwaitExpression expression) : base(expression)
    {
        _initialized = false;
    }

    protected override void Initialize(EvaluationContext context)
    {
        _awaitExpression = Build(((AwaitExpression) _expression).Argument);
    }

    // https://tc39.es/ecma262/#await
    protected override object EvaluateInternal(EvaluationContext context)
    {
        var engine = context.Engine;
        
        var asyncContext = engine._activeEvaluationContext;

        try
        {
            var value = _awaitExpression.GetValue(context);
            if (value is not PromiseInstance promise)
            {
                return value;
            }

            if (promise.State == PromiseState.Pending)
            {
                // TODO: Should we run this immediately or wait for the next attempt to resume? Is this necessary? This can avoid leaving and re-entering the execution context needlessly.
                engine.RunAvailableContinuations();
                
                if (promise.State == PromiseState.Pending)
                {
                    engine._suspend = true;
                    engine._suspendValue = promise;
                    return null!;
                }
            }
            
            return value.UnwrapIfPromise();
        }
        catch (PromiseRejectedException e)
        {
            ExceptionHelper.ThrowJavaScriptException(engine, e.RejectedValue, _expression.Location);
            return null;
        }
    }
}
