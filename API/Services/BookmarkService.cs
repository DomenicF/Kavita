﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using API.Constants;
using API.Data;
using API.DTOs.Reader;
using API.Entities;
using API.Entities.Enums;
using API.SignalR;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace API.Services;

public interface IBookmarkService
{
    Task DeleteBookmarkFiles(IEnumerable<AppUserBookmark> bookmarks);
    Task<bool> BookmarkPage(AppUser userWithBookmarks, BookmarkDto bookmarkDto, string imageToBookmark);
    Task<bool> RemoveBookmarkPage(AppUser userWithBookmarks, BookmarkDto bookmarkDto);
    Task<IEnumerable<string>> GetBookmarkFilesById(IEnumerable<int> bookmarkIds);
    [DisableConcurrentExecution(timeoutInSeconds: 2 * 60 * 60), AutomaticRetry(Attempts = 0)]
    Task ConvertAllBookmarkToWebP();
    Task ConvertAllCoverToWebP();
    Task ConvertBookmarkToWebP(int bookmarkId);

}

public class BookmarkService : IBookmarkService
{
    public const string Name = "BookmarkService";
    private readonly ILogger<BookmarkService> _logger;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDirectoryService _directoryService;
    private readonly IImageService _imageService;
    private readonly IEventHub _eventHub;

    public BookmarkService(ILogger<BookmarkService> logger, IUnitOfWork unitOfWork,
        IDirectoryService directoryService, IImageService imageService, IEventHub eventHub)
    {
        _logger = logger;
        _unitOfWork = unitOfWork;
        _directoryService = directoryService;
        _imageService = imageService;
        _eventHub = eventHub;
    }

    /// <summary>
    /// Deletes the files associated with the list of Bookmarks passed. Will clean up empty folders.
    /// </summary>
    /// <param name="bookmarks"></param>
    public async Task DeleteBookmarkFiles(IEnumerable<AppUserBookmark> bookmarks)
    {
        var bookmarkDirectory =
            (await _unitOfWork.SettingsRepository.GetSettingAsync(ServerSettingKey.BookmarkDirectory)).Value;

        var bookmarkFilesToDelete = bookmarks.Select(b => Tasks.Scanner.Parser.Parser.NormalizePath(
            _directoryService.FileSystem.Path.Join(bookmarkDirectory,
                b.FileName))).ToList();

        if (bookmarkFilesToDelete.Count == 0) return;

        _directoryService.DeleteFiles(bookmarkFilesToDelete);

        // Delete any leftover folders
        foreach (var directory in _directoryService.FileSystem.Directory.GetDirectories(bookmarkDirectory, "", SearchOption.AllDirectories))
        {
            if (_directoryService.FileSystem.Directory.GetFiles(directory, "", SearchOption.AllDirectories).Length == 0 &&
                _directoryService.FileSystem.Directory.GetDirectories(directory).Length == 0)
            {
                _directoryService.FileSystem.Directory.Delete(directory, false);
            }
        }
    }
    /// <summary>
    /// Creates a new entry in the AppUserBookmarks and copies an image to BookmarkDirectory.
    /// </summary>
    /// <param name="userWithBookmarks">An AppUser object with Bookmarks populated</param>
    /// <param name="bookmarkDto"></param>
    /// <param name="imageToBookmark">Full path to the cached image that is going to be copied</param>
    /// <returns>If the save to DB and copy was successful</returns>
    public async Task<bool> BookmarkPage(AppUser userWithBookmarks, BookmarkDto bookmarkDto, string imageToBookmark)
    {
        if (userWithBookmarks == null || userWithBookmarks.Bookmarks == null) return false;
        try
        {
            var userBookmark = userWithBookmarks.Bookmarks.SingleOrDefault(b => b.Page == bookmarkDto.Page && b.ChapterId == bookmarkDto.ChapterId);
            if (userBookmark != null)
            {
                _logger.LogError("Bookmark already exists for Series {SeriesId}, Volume {VolumeId}, Chapter {ChapterId}, Page {PageNum}", bookmarkDto.SeriesId, bookmarkDto.VolumeId, bookmarkDto.ChapterId, bookmarkDto.Page);
                return true;
            }

            var fileInfo = _directoryService.FileSystem.FileInfo.FromFileName(imageToBookmark);
            var settings = await _unitOfWork.SettingsRepository.GetSettingsDtoAsync();
            var targetFolderStem = BookmarkStem(userWithBookmarks.Id, bookmarkDto.SeriesId, bookmarkDto.ChapterId);
            var targetFilepath = Path.Join(settings.BookmarksDirectory, targetFolderStem);

            var bookmark = new AppUserBookmark()
            {
                Page = bookmarkDto.Page,
                VolumeId = bookmarkDto.VolumeId,
                SeriesId = bookmarkDto.SeriesId,
                ChapterId = bookmarkDto.ChapterId,
                FileName = Path.Join(targetFolderStem, fileInfo.Name),
                AppUserId = userWithBookmarks.Id
            };

            _directoryService.CopyFileToDirectory(imageToBookmark, targetFilepath);

            _unitOfWork.UserRepository.Add(bookmark);
            await _unitOfWork.CommitAsync();

            if (settings.ConvertBookmarkToWebP)
            {
                // Enqueue a task to convert the bookmark to webP
                BackgroundJob.Enqueue(() => ConvertBookmarkToWebP(bookmark.Id));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "There was an exception when saving bookmark");
           await _unitOfWork.RollbackAsync();
           return false;
        }

        return true;
    }

    /// <summary>
    /// Removes the Bookmark entity and the file from BookmarkDirectory
    /// </summary>
    /// <param name="userWithBookmarks"></param>
    /// <param name="bookmarkDto"></param>
    /// <returns></returns>
    public async Task<bool> RemoveBookmarkPage(AppUser userWithBookmarks, BookmarkDto bookmarkDto)
    {
        if (userWithBookmarks.Bookmarks == null) return true;
        var bookmarkToDelete = userWithBookmarks.Bookmarks.SingleOrDefault(x =>
            x.ChapterId == bookmarkDto.ChapterId && x.Page == bookmarkDto.Page);
        try
        {
            if (bookmarkToDelete != null)
            {
                _unitOfWork.UserRepository.Delete(bookmarkToDelete);
            }

            await _unitOfWork.CommitAsync();
        }
        catch (Exception)
        {
            return false;
        }

        await DeleteBookmarkFiles(new[] {bookmarkToDelete});
        return true;
    }

    public async Task<IEnumerable<string>> GetBookmarkFilesById(IEnumerable<int> bookmarkIds)
    {
        var bookmarkDirectory =
            (await _unitOfWork.SettingsRepository.GetSettingAsync(ServerSettingKey.BookmarkDirectory)).Value;

        var bookmarks = await _unitOfWork.UserRepository.GetAllBookmarksByIds(bookmarkIds.ToList());
        return bookmarks
            .Select(b => Tasks.Scanner.Parser.Parser.NormalizePath(_directoryService.FileSystem.Path.Join(bookmarkDirectory,
                b.FileName)));
    }

    /// <summary>
    /// This is a long-running job that will convert all bookmarks into WebP. Do not invoke anyway except via Hangfire.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 2 * 60 * 60), AutomaticRetry(Attempts = 0)]
    public async Task ConvertAllBookmarkToWebP()
    {
        var bookmarkDirectory =
            (await _unitOfWork.SettingsRepository.GetSettingAsync(ServerSettingKey.BookmarkDirectory)).Value;

        await _eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
            MessageFactory.ConvertBookmarksProgressEvent(0F, ProgressEventType.Started));
        var bookmarks = (await _unitOfWork.UserRepository.GetAllBookmarksAsync())
            .Where(b => !b.FileName.EndsWith(".webp")).ToList();

        var count = 1F;
        foreach (var bookmark in bookmarks)
        {
            bookmark.FileName = await SaveAsWebP(bookmarkDirectory, bookmark.FileName,
                BookmarkStem(bookmark.AppUserId, bookmark.SeriesId, bookmark.ChapterId));
            _unitOfWork.UserRepository.Update(bookmark);
            await _unitOfWork.CommitAsync();
            await _eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
                MessageFactory.ConvertBookmarksProgressEvent(count / bookmarks.Count, ProgressEventType.Started));
            count++;
        }

        await _eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
            MessageFactory.ConvertBookmarksProgressEvent(1F, ProgressEventType.Ended));

        _logger.LogInformation("[BookmarkService] Converted bookmarks to WebP");
    }

    /// <summary>
    /// This is a long-running job that will convert all covers into WebP. Do not invoke anyway except via Hangfire.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 2 * 60 * 60), AutomaticRetry(Attempts = 0)]
    public async Task ConvertAllCoverToWebP()
    {
        var coverDirectory = _directoryService.CoverImageDirectory;

        await _eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
            MessageFactory.ConvertCoverProgressEvent(0F, ProgressEventType.Started));
        var chapters = await _unitOfWork.ChapterRepository.GetAllChaptersWithNonWebPCovers();

        var count = 1F;
        foreach (var chapter in chapters)
        {
            var newFile = await SaveAsWebP(coverDirectory, chapter.CoverImage, coverDirectory);
            chapter.CoverImage = newFile;
            _unitOfWork.ChapterRepository.Update(chapter);
            await _unitOfWork.CommitAsync();
            await _eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
                MessageFactory.ConvertCoverProgressEvent(count / chapters.Count, ProgressEventType.Started));
            count++;
        }

        await _eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
            MessageFactory.ConvertCoverProgressEvent(1F, ProgressEventType.Ended));

        _logger.LogInformation("[BookmarkService] Converted covers to WebP");
    }

    /// <summary>
    /// This is a job that runs after a bookmark is saved
    /// </summary>
    public async Task ConvertBookmarkToWebP(int bookmarkId)
    {
        var bookmarkDirectory =
            (await _unitOfWork.SettingsRepository.GetSettingAsync(ServerSettingKey.BookmarkDirectory)).Value;
        var convertBookmarkToWebP =
            (await _unitOfWork.SettingsRepository.GetSettingsDtoAsync()).ConvertBookmarkToWebP;

        if (!convertBookmarkToWebP) return;

        // Validate the bookmark still exists
        var bookmark = await _unitOfWork.UserRepository.GetBookmarkAsync(bookmarkId);
        if (bookmark == null) return;

        bookmark.FileName = await SaveAsWebP(bookmarkDirectory, bookmark.FileName,
            BookmarkStem(bookmark.AppUserId, bookmark.SeriesId, bookmark.ChapterId));
        _unitOfWork.UserRepository.Update(bookmark);

        await _unitOfWork.CommitAsync();
    }

    /// <summary>
    /// Converts an image file, deletes original and returns the new path back
    /// </summary>
    /// <param name="imageDirectory">Full Path to where files are stored</param>
    /// <param name="filename">The file to convert</param>
    /// <param name="targetFolder">Full path to where files should be stored or any stem</param>
    /// <returns></returns>
    private async Task<string> SaveAsWebP(string imageDirectory, string filename, string targetFolder)
    {
        var fullSourcePath = _directoryService.FileSystem.Path.Join(imageDirectory, filename);
        var fullTargetDirectory = fullSourcePath.Replace(new FileInfo(filename).Name, string.Empty);

        var newFilename = string.Empty;
        _logger.LogDebug("Converting {Source} image into WebP at {Target}", fullSourcePath, fullTargetDirectory);

        try
        {
            // Convert target file to webp then delete original target file and update bookmark

            var originalFile = filename;
            try
            {
                var targetFile = await _imageService.ConvertToWebP(fullSourcePath, fullTargetDirectory);
                var targetName = new FileInfo(targetFile).Name;
                newFilename = Path.Join(targetFolder, targetName);
                _directoryService.DeleteFiles(new[] {fullSourcePath});
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not convert image {FilePath}", filename);
                newFilename = originalFile;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not convert image to WebP");
        }

        return newFilename;
    }

    private static string BookmarkStem(int userId, int seriesId, int chapterId)
    {
        return Path.Join($"{userId}", $"{seriesId}", $"{chapterId}");
    }
}
