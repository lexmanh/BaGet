using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NuGet.Versioning;

namespace BaGet.Core
{
    public class PackageService : IPackageService
    {
        private readonly IPackageDatabase _db;
        private readonly IUpstreamClient _upstream;
        private readonly IPackageIndexingService _indexer;
        private readonly ILogger<PackageService> _logger;
        private readonly string _storePath;

        public PackageService(
            IPackageDatabase db,
            IUpstreamClient upstream,
            IPackageIndexingService indexer,
            ILogger<PackageService> logger,
            IOptionsSnapshot<FileSystemStorageOptions> options)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _upstream = upstream ?? throw new ArgumentNullException(nameof(upstream));
            _indexer = indexer ?? throw new ArgumentNullException(nameof(indexer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            
            if (options == null) throw new ArgumentNullException(nameof(options));

            // Resolve relative path components ('.'/'..') and ensure there is a trailing slash.
            _storePath = Path.GetFullPath(options.Value.Path);
            if (!_storePath.EndsWith(Path.DirectorySeparatorChar.ToString()))
                _storePath += Path.DirectorySeparatorChar;
        }

        public async Task<IReadOnlyList<NuGetVersion>> FindPackageVersionsAsync(
            string id,
            CancellationToken cancellationToken)
        {
            var upstreamVersions = await _upstream.ListPackageVersionsAsync(id, cancellationToken);

            // Merge the local package versions into the upstream package versions.
            var localPackages = await _db.FindAsync(id, includeUnlisted: true, cancellationToken);
            var localVersions = localPackages.Select(p => p.Version);

            if (!upstreamVersions.Any()) return localVersions.ToList();
            if (!localPackages.Any()) return upstreamVersions;

            return upstreamVersions.Concat(localVersions).Distinct().ToList();
        }

        public async Task<IReadOnlyList<Package>> FindPackagesAsync(string id, CancellationToken cancellationToken)
        {
            var upstreamPackages = await _upstream.ListPackagesAsync(id, cancellationToken);
            var localPackages = await _db.FindAsync(id, includeUnlisted: true, cancellationToken);

            if (!upstreamPackages.Any()) return localPackages;
            if (!localPackages.Any()) return upstreamPackages;

            // Merge the local packages into the upstream packages.
            var result = upstreamPackages.ToDictionary(p => p.Version);
            var local = localPackages.ToDictionary(p => p.Version);

            foreach (var localPackage in local)
            {
                result[localPackage.Key] = localPackage.Value;
            }

            return result.Values.ToList();
        }

        public async Task<Package> FindPackageOrNullAsync(
            string id,
            NuGetVersion version,
            CancellationToken cancellationToken)
        {
            if (!await MirrorAsync(id, version, cancellationToken))
            {
                return null;
            }

            return await _db.FindOrNullAsync(id, version, includeUnlisted: true, cancellationToken);
        }

        public async Task<bool> ExistsAsync(string id, NuGetVersion version, CancellationToken cancellationToken)
        {
            return await MirrorAsync(id, version, cancellationToken);
        }

        public async Task AddDownloadAsync(string packageId, NuGetVersion version, CancellationToken cancellationToken)
        {
            await _db.AddDownloadAsync(packageId, version, cancellationToken);
        }

        /// <summary>
        /// Index the package from an upstream if it does not exist locally.
        /// </summary>
        /// <param name="id">The package ID to index from an upstream.</param>
        /// <param name="version">The package version to index from an upstream.</param>
        /// <param name="cancellationToken"></param>
        /// <returns>True if the package exists locally or was indexed from an upstream source.</returns>
        private async Task<bool> MirrorAsync(string id, NuGetVersion version, CancellationToken cancellationToken)
        {
            var dbExist = await _db.ExistsAsync(id, version, cancellationToken);
            
            // Check if the package exists locally, including missing packages in filesystem storage.
            if (dbExist)
            {
                // example path:
                // /var/baget/packages/packages/farmerp.httpapi/1.0.0-preview-20231115-113309/farmerp.httpapi.1.0.0-preview-20231115-113309.nupkg
                var packagePath = GetFullPath(
                    Path.Combine("packages", 
                        id.ToLowerInvariant(), 
                        version.ToNormalizedString().ToLowerInvariant(), 
                        $"{id.ToLowerInvariant()}.{version.ToNormalizedString().ToLowerInvariant()}.nupkg"));
                
                
                if (File.Exists(packagePath))
                {
                    // _logger.LogInformation(
                    //     "Package {PackageId} {PackageVersion} exists locally at {PackagePath}",
                    //     id,
                    //     version,
                    //     packagePath);
                    
                    return true;
                }
                
                _logger.LogWarning(
                    "Package {PackageId} {PackageVersion} does not exist locally at {PackagePath}, but exists in database. " +
                    "This is likely due to a missing package file in the filesystem storage",
                    id,
                    version,
                    packagePath);
            }
            

            _logger.LogInformation(
                "Package {PackageId} {PackageVersion} does not exist locally. Checking upstream feed...",
                id,
                version);

            try
            {
                using (var packageStream = await _upstream.DownloadPackageOrNullAsync(id, version, cancellationToken))
                {
                    if (packageStream == null)
                    {
                        _logger.LogWarning(
                            "Upstream feed does not have package {PackageId} {PackageVersion}",
                            id,
                            version);
                        return false;
                    }

                    _logger.LogInformation(
                        "Downloaded package {PackageId} {PackageVersion}, indexing...",
                        id,
                        version);

                    var result = await _indexer.IndexAsync(packageStream, cancellationToken);

                    _logger.LogInformation(
                        "Finished indexing package {PackageId} {PackageVersion} from upstream feed with result {Result}",
                        id,
                        version,
                        result);

                    return result == PackageIndexingResult.Success;
                }
            }
            catch (Exception e)
            {
                _logger.LogError(
                    e,
                    "Failed to index package {PackageId} {PackageVersion} from upstream",
                    id,
                    version);

                return false;
            }
        }
        
        private string GetFullPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("Path is required", nameof(path));
            }

            var fullPath = Path.GetFullPath(Path.Combine(_storePath, path));

            // Verify path is under the _storePath.
            if (!fullPath.StartsWith(_storePath, StringComparison.Ordinal) ||
                fullPath.Length == _storePath.Length)
            {
                throw new ArgumentException("Path resolves outside store path", nameof(path));
            }

            return fullPath;
        }
    }
}
