namespace PersonalServerManager.Application.Models;

public sealed record Ec2InstanceResource(
    string InstanceId,
    string Name,
    string InstanceType,
    string State);

public sealed record RdsInstanceResource(
    string DbInstanceIdentifier,
    string Engine,
    string Status);

public sealed record S3BucketResource(string Name, string Region);
