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
}
