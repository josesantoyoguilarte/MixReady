public interface IFileStorageService
{
    Task<string> SaveOriginalAsync(IFormFile file);
    string GetProcessedPath(Guid trackId);
    string GetStemsPath(Guid trackId);
    string GetKitchenOutputPath(Guid id);
}
