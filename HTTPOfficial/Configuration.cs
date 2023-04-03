namespace HTTPOfficial;

public record struct Configuration(
    int Port,
    bool UseHttps,
    string CertPath,
    string KeyPath,
    string SmtpHost,
    string EmailUsername,
    string EmailPassword,
    InstanceRange[] InstanceRanges,
    string InstanceKey);