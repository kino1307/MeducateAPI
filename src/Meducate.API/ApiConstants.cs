namespace Meducate.API;

internal static class ApiConstants
{
    internal const int DefaultPageSize = 50;
    internal const int MaxPageSize = 200;
    internal const int MaxQueryLength = 200;
    internal const int MaxKeyNameLength = 100;
    internal const int MaxKeysPerOrg = 5;
    internal const int MaxAuthAgeSeconds = 7 * 24 * 60 * 60;
    internal const int StampCheckIntervalSeconds = 300;
    internal const int FreshAuthWindowSeconds = 600;
    internal const double UsageWarningThreshold = 0.80;
}
