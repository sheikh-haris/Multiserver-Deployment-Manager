namespace IisControlCenter.Web.Deployments;

/// <summary>Settings for deployments. Section "Deployments" in configuration.</summary>
public sealed class DeploymentOptions
{
    public const string Section = "Deployments";

    /// <summary>Folder for stored packages. Must be outside any web root and backed up by you. Needs free space for retention x package size.</summary>
    public string PackageStorePath { get; set; } = "App_Data/packages";
    public int MaxPackageMb { get; set; } = 500;
    public int ChunkMb { get; set; } = 8;
    public int RetentionDays { get; set; } = 90;
    public int BackupsToKeep { get; set; } = 5;
    public int RequestLifetimeDays { get; set; } = 7;
    public int HealthTimeoutSeconds { get; set; } = 60;
    public int AgentOfflineGraceMinutes { get; set; } = 60;
    public int RunTimeoutMinutes { get; set; } = 60;
    public int UploadSessionHours { get; set; } = 24;
    public int MaxEntries { get; set; } = 50_000;
    public int MaxUncompressedMb { get; set; } = 5120;
    /// <summary>Auto: scan on the portal if Defender can run here, otherwise on the target server. Portal: refuse if the portal cannot scan. Agent: always scan on the target.</summary>
    public string AntivirusMode { get; set; } = "Auto";
    /// <summary>Base address used in email links, for example https://portal.example.local</summary>
    public string? PublicBaseUrl { get; set; }

    public long MaxPackageBytes => MaxPackageMb * 1024L * 1024;
    public int ChunkBytes => ChunkMb * 1024 * 1024;
}

public sealed class SmtpOptions
{
    public const string Section = "Notifications:Smtp";
    public string? Host { get; set; }
    public int Port { get; set; } = 25;
    public bool UseSsl { get; set; }
    public string From { get; set; } = "iis-control-center@localhost";
    public string? User { get; set; }
    /// <summary>Do not put in a file: supply from Key Vault or an environment variable (Notifications__Smtp__Password).</summary>
    public string? Password { get; set; }
}
