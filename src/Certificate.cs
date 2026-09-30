namespace CertRotationDrill;

public sealed record CertificateDescription(
    string SerialNumber,
    string KeyId,
    string Subject,
    IReadOnlyList<string> SubjectAlternativeNames,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string Thumbprint);

public sealed record IssuedCertificate(
    CertificateDescription Description,
    string PrivateKeyPlaceholder);
