using JetBrains.Application.BuildScript.Application.Zones;
using JetBrains.Rider.Backend.Env;
using JetBrains.Rider.Backend.Product;

namespace RiderVcxFix.Backend;

// The Shell worker must initialize in the full Rider backend before a solution exists.
[ZoneMarker]
public sealed class ZoneMarker : IRequire<IRiderFeatureZone>, IRequire<IRiderProductEnvironmentZone>
{
}
