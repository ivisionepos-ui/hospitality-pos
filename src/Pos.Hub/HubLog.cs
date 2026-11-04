namespace Pos.Hub;

/// <summary>The hub's log messages.</summary>
public static partial class HubLog
{
    /// <summary>Certificate summary at start-up.</summary>
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Shop certificate: {Certificates}")]
    public static partial void CertificatesLoaded(ILogger logger, Security.HubCertificates certificates);

    /// <summary>The hub certificate was renewed.</summary>
    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Hub certificate renewed: {Certificates}")]
    public static partial void CertificateRenewed(ILogger logger, Security.HubCertificates certificates);

    /// <summary>Renewal failed; the current certificate stays in use.</summary>
    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Could not renew the hub certificate; the current one stays in use.")]
    public static partial void CertificateRenewalFailed(ILogger logger, Exception exception);

    /// <summary>A live event could not be sent; screens will fetch it on catch-up.</summary>
    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Live event {Seq} could not be sent; screens will catch up.")]
    public static partial void LiveSendFailed(ILogger logger, long seq, Exception exception);

    /// <summary>A backup was made and checked.</summary>
    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "Backup made: {File} ({Reason})")]
    public static partial void BackupMade(ILogger logger, string file, string reason);

    /// <summary>A backup could not be made.</summary>
    [LoggerMessage(EventId = 21, Level = LogLevel.Error, Message = "Backup FAILED ({Reason}): {Error}")]
    public static partial void BackupFailed(ILogger logger, string reason, string error);

    /// <summary>The second copy of a backup could not be made.</summary>
    [LoggerMessage(EventId = 22, Level = LogLevel.Warning, Message = "Backup copy to {Folder} FAILED: {Error}")]
    public static partial void BackupCopyFailed(ILogger logger, string folder, string error);

    /// <summary>An address file was loaded.</summary>
    [LoggerMessage(EventId = 30, Level = LogLevel.Information, Message = "Address file {File} loaded: {Addresses} addresses in {Postcodes} postcodes")]
    public static partial void AddressFileLoaded(ILogger logger, string file, int addresses, int postcodes);

    /// <summary>An address file could not be loaded.</summary>
    [LoggerMessage(EventId = 31, Level = LogLevel.Warning, Message = "Address file {File} could not be loaded: {Error}")]
    public static partial void AddressFileFailed(ILogger logger, string file, string error);
}
