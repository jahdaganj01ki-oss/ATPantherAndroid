namespace ATPanther.Windows;

public static class AppConfig
{
    public const string AppName = "ATPanther";
    public const string DefaultPhoneEnvironmentKey = "ATPANTHER_PHONE";
    public const string DefaultPasswordEnvironmentKey = "ATPANTHER_PASSWORD";
    public const int DefaultThresholdMb = 850;
    public const int DefaultIntervalSeconds = 60;
    public const int TopUpSizeMb = 1024;

    public const string CredentialPhoneKey = "phone";
    public const string CredentialPasswordKey = "password";
    public const string CredentialThresholdMbKey = "thresholdMb";
    public const string CredentialIntervalSecondsKey = "intervalSeconds";
}
