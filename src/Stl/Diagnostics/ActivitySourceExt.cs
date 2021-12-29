using System.Diagnostics;

namespace Stl.Diagnostics;

public static class ActivitySourceExt
{
    public static Activity? StartActivity(
        this ActivitySource activitySource,
        Type sourceType,
        string operationName,
        ActivityKind activityKind = ActivityKind.Internal)
        => activitySource.StartActivity(
            sourceType.GetOperationName(operationName),
            activityKind);
}
