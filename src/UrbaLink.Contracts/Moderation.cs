namespace UrbaLink.Contracts;
public sealed record ReportRequest(Guid RequestId,Guid CommunityId,string Kind,long TargetId,string Reason);
public sealed record ReportReceipt(long Id);
