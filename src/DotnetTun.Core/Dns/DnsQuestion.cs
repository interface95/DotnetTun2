namespace DotnetTun.Core.Dns;

public sealed record DnsQuestion(ushort TransactionId, string Domain, DnsRecordType RecordType, byte[] OriginalQuestion);
