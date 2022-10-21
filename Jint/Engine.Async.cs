using Jint.Native.Promise;

namespace Jint;

public sealed partial class Engine
{
    internal bool _suspend;
    internal PromiseInstance? _suspendValue;
}
