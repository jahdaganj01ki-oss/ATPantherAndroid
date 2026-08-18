namespace ATPanther.AldiTalk;

/// <summary>Vertragsinformationen aus der navigation-list (customer-master-data BFF).</summary>
public sealed record ContractInfo(string ContractId, string? SubscriptionId, string? Msisdn);

/// <summary>Ergebnis der Volumen-Abfrage inklusive Fehlerdetails fürs Log.</summary>
public sealed record VolumeQueryResult(DataStatus? Status, int HttpStatus, string ErrorDetail);

/// <summary>Ergebnis der Abfrage des verbleibenden Datenvolumens (Analogie zu DataStatus.kt).</summary>
public sealed record DataStatus(
    double RemainingMb,
    string OfferId,
    string SubscriptionId,
    string ResourceId,
    string OnDemandAmount,
    string RefillThreshold);

/// <summary>Ergebnis einer 1-GB-Nachbuchung (Analogie zu BookingResult.kt).</summary>
public sealed record BookingResult(bool Success, bool IsUpdated, int StatusCode, string Message);

/// <summary>Ergebnis des kompletten Login-Vorgangs inklusive einsatzbereitem HTTP-Client.</summary>
public sealed record LoginResult(bool Success, HttpClient? Client, string? Error);
