using System.IO;
using System.Net;

namespace STool.Modules.LanTransfer;

internal sealed record NetworkEndpoint(IPAddress Address, IPAddress Mask, string InterfaceName)
{
    public string DisplayAddress => Address.ToString();
}

internal sealed record AuthExchangeRequest(string? Token, string? Code, bool Remember);

internal sealed record UploadBatchCreateRequest(string Name, int FileCount, long TotalBytes, bool IsFolder);

internal sealed record UploadBatchCreateResponse(string Id);

internal sealed record UploadInitRequest(
    string Name,
    string? RelativePath,
    long Size,
    long LastModified,
    string? BatchId = null);

internal sealed record UploadInitResponse(string Id, int ChunkSize, long Offset, string Name, bool Complete);

internal sealed record UploadStatusResponse(string Id, long Offset, long Size, bool Complete, string Name);

internal sealed record SharedFileDto(
    string Id,
    string Name,
    string RelativePath,
    long Size,
    long ModifiedUtc,
    bool IsFolder,
    int FileCount);

internal sealed record FolderContentsDto(
    string FolderId,
    string Name,
    string Path,
    IReadOnlyList<FolderItemDto> Items);

internal sealed record FolderItemDto(
    string Name,
    string RelativePath,
    long Size,
    bool IsFolder,
    int FileCount);

internal sealed record ArchiveCreateRequest(string? FileId);

internal sealed record PreparedArchiveStatus(
    string Id,
    string Name,
    string Status,
    long ProcessedBytes,
    long TotalBytes,
    long Size,
    int FileCount,
    string? Error,
    string? DownloadUrl);

/// <summary>PreparedArchiveStatus.Status 在网页接口中的取值。</summary>
internal static class ArchiveStatusNames
{
    public const string Preparing = "preparing";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

internal enum ArchiveProgressStage
{
    Preparing,
    Ready,
    Canceled,
    Failed
}

internal sealed record ArchiveProgressInfo(
    string Id,
    string Name,
    long Processed,
    long Total,
    ArchiveProgressStage Stage);

internal sealed record ReceivedBatchInfo(
    string Id,
    string Name,
    string FullPath,
    long Size,
    int FileCount,
    bool IsDirectory);

internal enum TransferDirection
{
    ToPhone,
    ToComputer
}

internal enum OutgoingTransferMode
{
    Automatic,
    Separate,
    CombinedArchive
}

internal enum TransferState
{
    Preparing,
    AwaitingConfirmation,
    Transferring,
    Paused,
    WaitingForResume,
    Completed,
    Canceled,
    Rejected,
    Failed
}

internal sealed record TransferSessionSnapshot(
    string Id,
    string Name,
    TransferDirection Direction,
    TransferState State,
    long TransferredBytes,
    long TotalBytes,
    double BytesPerSecond,
    int FileCount,
    IReadOnlyList<string> SourcePaths,
    string? DestinationPath,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    string? Error,
    string? DownloadUrl = null,
    IReadOnlyList<TransferDownloadDto>? Downloads = null);

internal sealed record TransferHistoryEntry(
    string Id,
    string Name,
    TransferDirection Direction,
    long TotalBytes,
    int FileCount,
    string LocationPath,
    bool LocationIsDirectory,
    DateTimeOffset CompletedUtc);

internal sealed record TransferOfferDto(
    string Id,
    string Name,
    long TotalBytes,
    int FileCount,
    int ItemCount,
    string? DownloadUrl,
    bool CanBrowse,
    bool Separate,
    IReadOnlyList<TransferDownloadDto> Downloads);

internal sealed record TransferDownloadDto(
    string Id,
    string Name,
    long Size,
    string DownloadUrl,
    bool Started,
    bool Completed);

internal sealed record FolderBrowseResponse(string FolderId, string Name);

internal sealed class SharedFileEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string RelativePath { get; init; }
    public required string FullPath { get; init; }
    public required long Size { get; init; }
    public required DateTimeOffset ModifiedUtc { get; init; }
    public required bool IsFolder { get; init; }
    public required IReadOnlyList<SharedFolderFile> FolderFiles { get; init; }

    public int FileCount => IsFolder ? FolderFiles.Count : 1;
    public bool IsAvailable => IsFolder ? Directory.Exists(FullPath) : File.Exists(FullPath);

    public SharedFileDto ToDto() => new(
        Id,
        Name,
        RelativePath,
        Size,
        ModifiedUtc.ToUnixTimeMilliseconds(),
        IsFolder,
        FileCount);
}

internal sealed record SharedFolderFile(string FullPath, string RelativePath, long Size);

internal sealed record PreparedArchiveItem(string? FullPath, string EntryName, long Size);

internal sealed record PreparedArchiveDownload(string Id, string Name, string FullPath);
