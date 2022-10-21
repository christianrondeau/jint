using Jint.Native;

namespace Jint.Tests.Runtime;

public class AsyncTests
{
    private class AsyncResult
    {
        public int Value { get; set; }
    }
    
    [Fact]
    public void RegisterPromise_WithAwait_SuspendAndResume()
    {
        var result = new AsyncResult();
        Action<JsValue> resolveFunc = null;

        var engine = new Engine();
        engine.SetValue('f', new Func<JsValue>(() =>
        {
            var (promise, resolve, _) = engine.RegisterPromise();
            resolveFunc = resolve;
            return promise;
        }));

        engine.SetValue("result", JsValue.FromObject(engine, result));        
        engine.Evaluate("async function test() { const x = await f(); result.afterAwaitCalled = x; } test();");

        Assert.Equal(0, result.Value);
        resolveFunc(1);
        Assert.Equal(1, result.Value);
    }
}
