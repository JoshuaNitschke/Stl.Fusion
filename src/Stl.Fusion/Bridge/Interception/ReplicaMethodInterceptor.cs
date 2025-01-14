using Castle.DynamicProxy;
using Stl.Fusion.Interception;
using Stl.Interception.Interceptors;
using Stl.Versioning;

namespace Stl.Fusion.Bridge.Interception;

public class ReplicaMethodInterceptor : ComputeMethodInterceptorBase
{
    public new record Options : ComputeMethodInterceptorBase.Options
    {
        public VersionGenerator<LTag>? VersionGenerator { get; init; }
    }

    protected readonly IReplicator Replicator;
    protected readonly VersionGenerator<LTag> VersionGenerator;

    public ReplicaMethodInterceptor(Options options, IServiceProvider services)
        : base(options, services)
    {
        Replicator = services.GetRequiredService<IReplicator>();
        VersionGenerator = options.VersionGenerator ?? services.VersionGenerator<LTag>();
    }

    protected override ComputeFunctionBase<T> CreateFunction<T>(ComputeMethodDef method)
        => new ReplicaMethodFunction<T>(method, Replicator, VersionGenerator);

    protected override MethodDef? CreateMethodDef(MethodInfo methodInfo, IInvocation initialInvocation)
        => base.CreateMethodDef(methodInfo, initialInvocation)?.ToReplicaMethodDef();

    protected override void ValidateTypeInternal(Type type) { }
}
