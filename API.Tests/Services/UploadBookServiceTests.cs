using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions.TestingHelpers;
using System.Linq;
using System.Threading.Tasks;
using API.Data;
using API.Data.Metadata;
using API.DTOs.Uploads;
using API.Entities;
using API.Entities.Enums;
using API.Services;
using API.Services.MetadataEnrichment;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace API.Tests.Services;

public class UploadBookServiceTests(ITestOutputHelper outputHelper) : AbstractDbTest(outputHelper)
{
    private readonly ILogger<UploadBookService> _logger = Substitute.For<ILogger<UploadBookService>>();

    private UploadBookService CreateService(
        IUnitOfWork unitOfWork,
        MockFileSystem? fileSystem = null,
        IArchiveService? archiveService = null,
        IBookService? bookService = null,
        ITaskScheduler? taskScheduler = null,
        IMetadataEnrichmentService? enrichmentService = null)
    {
        fileSystem ??= CreateFileSystem();
        archiveService ??= Substitute.For<IArchiveService>();
        bookService ??= Substitute.For<IBookService>();
        taskScheduler ??= Substitute.For<ITaskScheduler>();
        enrichmentService ??= CreateDefaultEnrichmentService();

        var directoryService = new DirectoryService(Substitute.For<ILogger<DirectoryService>>(), fileSystem);

        return new UploadBookService(directoryService, archiveService, bookService,
            unitOfWork, taskScheduler, enrichmentService, _logger);
    }

    private static IMetadataEnrichmentService CreateDefaultEnrichmentService()
    {
        var mock = Substitute.For<IMetadataEnrichmentService>();
        mock.EnrichAsync(Arg.Any<EnrichmentContext>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(new EnrichmentResult { Source = MetadataSource.Local, Success = false });
        mock.SearchAsync(Arg.Any<EnrichmentContext>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(new List<EnrichmentResult>());

        return mock;
    }

    private static IFormFile CreateMockFormFile(string fileName, string content = "dummy")
    {
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var formFile = Substitute.For<IFormFile>();
        formFile.FileName.Returns(fileName);
        formFile.Length.Returns(stream.Length);
        formFile.OpenReadStream().Returns(stream);
        formFile.CopyToAsync(Arg.Any<Stream>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(callInfo =>
            {
                stream.Position = 0;
                return stream.CopyToAsync(callInfo.Arg<Stream>());
            });

        return formFile;
    }

    #region ProcessUploadsAsync

    [Fact]
    public async Task ProcessUploadsAsync_WithArchiveFile_ReturnsArchiveFormat()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var archiveService = Substitute.For<IArchiveService>();
        archiveService.GetComicInfo(Arg.Any<string>()).Returns(new ComicInfo
        {
            Series = "My Manga",
            Volume = "1",
            Number = "5",
            Title = "The Beginning",
            Writer = "Author A",
            Summary = "A great start"
        });

        var service = CreateService(unitOfWork, archiveService: archiveService);
        var files = new List<IFormFile> { CreateMockFormFile("test.cbz") };

        var results = await service.ProcessUploadsAsync(files);

        Assert.Single(results);
        var result = results[0];
        Assert.Equal("test.cbz", result.OriginalFileName);
        Assert.Equal(MangaFormat.Archive, result.Format);
        Assert.Equal("My Manga", result.Series);
        Assert.Equal("1", result.Volume);
        Assert.Equal("5", result.Number);
        Assert.Equal("The Beginning", result.Title);
        Assert.Equal("Author A", result.Writer);
        Assert.Equal("A great start", result.Summary);
    }

    [Fact]
    public async Task ProcessUploadsAsync_WithEpubFile_ReturnsEpubFormat()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var bookService = Substitute.For<IBookService>();
        bookService.GetComicInfo(Arg.Any<string>()).Returns(new ComicInfo
        {
            Series = "My Book",
            Title = "Chapter One",
            Writer = "Author B"
        });

        var service = CreateService(unitOfWork, bookService: bookService);
        var files = new List<IFormFile> { CreateMockFormFile("novel.epub") };

        var results = await service.ProcessUploadsAsync(files);

        Assert.Single(results);
        Assert.Equal(MangaFormat.Epub, results[0].Format);
        Assert.Equal("My Book", results[0].Series);
    }

    [Fact]
    public async Task ProcessUploadsAsync_WithPdfFile_ReturnsPdfFormat()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var bookService = Substitute.For<IBookService>();
        bookService.GetComicInfo(Arg.Any<string>()).Returns(new ComicInfo
        {
            Series = "PDF Comic",
            Title = "Issue 1"
        });

        var service = CreateService(unitOfWork, bookService: bookService);
        var files = new List<IFormFile> { CreateMockFormFile("comic.pdf") };

        var results = await service.ProcessUploadsAsync(files);

        Assert.Single(results);
        Assert.Equal(MangaFormat.Pdf, results[0].Format);
        Assert.Equal("PDF Comic", results[0].Series);
    }

    [Fact]
    public async Task ProcessUploadsAsync_WithUnsupportedExtension_SkipsFile()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var service = CreateService(unitOfWork);
        var files = new List<IFormFile> { CreateMockFormFile("readme.txt") };

        var results = await service.ProcessUploadsAsync(files);

        Assert.Empty(results);
    }

    [Fact]
    public async Task ProcessUploadsAsync_WithNoMetadata_FallsBackToFilename()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var archiveService = Substitute.For<IArchiveService>();
        archiveService.GetComicInfo(Arg.Any<string>()).Returns((ComicInfo?)null);

        var service = CreateService(unitOfWork, archiveService: archiveService);
        var files = new List<IFormFile> { CreateMockFormFile("My Cool Series v01.cbz") };

        var results = await service.ProcessUploadsAsync(files);

        Assert.Single(results);
        // Parser.ParseSeries extracts just the series name, stripping the volume indicator
        Assert.Equal("My Cool Series", results[0].Series);
        // Parser.ParseVolume extracts the volume number from "v01"
        Assert.Equal("1", results[0].Volume);
        Assert.Equal(string.Empty, results[0].Number);
    }

    [Fact]
    public async Task ProcessUploadsAsync_SuggestsLibrary()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var service = CreateService(unitOfWork);
        var files = new List<IFormFile> { CreateMockFormFile("test.cbz") };

        var results = await service.ProcessUploadsAsync(files);

        Assert.Single(results);
        // The seeded database has a "Manga" library with Archive file type
        Assert.NotNull(results[0].SuggestedLibraryId);
    }

    [Fact]
    public async Task ProcessUploadsAsync_MultipleFiles_ReturnsAll()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var service = CreateService(unitOfWork);
        var files = new List<IFormFile>
        {
            CreateMockFormFile("file1.cbz"),
            CreateMockFormFile("file2.epub"),
            CreateMockFormFile("file3.pdf"),
        };

        var results = await service.ProcessUploadsAsync(files);

        Assert.Equal(3, results.Count);
    }

    #endregion

    #region ConfirmUploadsAsync

    [Fact]
    public async Task ConfirmUploadsAsync_PlacesFileInCorrectDirectory()
    {
        var (unitOfWork, context, _) = await CreateDatabase();

        // Add a user and assign to the library
        var user = new AppUser { UserName = "testuser" };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var library = await context.Library.Include(l => l.AppUsers).FirstAsync();
        library.AppUsers.Add(user);
        await context.SaveChangesAsync();

        var fileSystem = CreateFileSystem();
        // Create a temp file
        var tempFileName = "abc123.cbz";
        fileSystem.AddFile(TempDirectory + tempFileName, new MockFileData("content"));

        var taskScheduler = Substitute.For<ITaskScheduler>();
        var service = CreateService(unitOfWork, fileSystem, taskScheduler: taskScheduler);

        var dto = new ConfirmUploadDto
        {
            LibraryId = library.Id,
            Files = new List<ConfirmUploadFileDto>
            {
                new()
                {
                    TempFileName = tempFileName,
                    OriginalFileName = "MyManga Vol 1.cbz",
                    Series = "MyManga",
                    Volume = "1",
                    Number = ""
                }
            }
        };

        await service.ConfirmUploadsAsync(dto, user.Id);

        // Verify file was moved to the correct location
        var expectedPath = Path.Combine(DataDirectory, "MyManga", "MyManga Vol 1.cbz");
        Assert.True(fileSystem.File.Exists(expectedPath));
        Assert.False(fileSystem.File.Exists(TempDirectory + tempFileName));

        // Verify scan was triggered
        await taskScheduler.Received(1).ScanLibrary(library.Id);
    }

    [Fact]
    public async Task ConfirmUploadsAsync_ThrowsOnMissingLibrary()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var service = CreateService(unitOfWork);

        var dto = new ConfirmUploadDto
        {
            LibraryId = 999,
            Files = new List<ConfirmUploadFileDto>()
        };

        await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmUploadsAsync(dto, 1));
    }

    [Fact]
    public async Task ConfirmUploadsAsync_ThrowsOnMissingTempFile()
    {
        var (unitOfWork, context, _) = await CreateDatabase();

        var user = new AppUser { UserName = "testuser" };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var library = await context.Library.Include(l => l.AppUsers).FirstAsync();
        library.AppUsers.Add(user);
        await context.SaveChangesAsync();

        var service = CreateService(unitOfWork);

        var dto = new ConfirmUploadDto
        {
            LibraryId = library.Id,
            Files = new List<ConfirmUploadFileDto>
            {
                new()
                {
                    TempFileName = "nonexistent.cbz",
                    OriginalFileName = "test.cbz",
                    Series = "Test",
                    Volume = "",
                    Number = ""
                }
            }
        };

        await Assert.ThrowsAsync<FileNotFoundException>(() => service.ConfirmUploadsAsync(dto, user.Id));
    }

    [Fact]
    public async Task ConfirmUploadsAsync_ThrowsOnDuplicateFile()
    {
        var (unitOfWork, context, _) = await CreateDatabase();

        var user = new AppUser { UserName = "testuser" };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var library = await context.Library.Include(l => l.AppUsers).FirstAsync();
        library.AppUsers.Add(user);
        await context.SaveChangesAsync();

        var fileSystem = CreateFileSystem();
        var tempFileName = "abc123.cbz";
        fileSystem.AddFile(TempDirectory + tempFileName, new MockFileData("content"));
        // Pre-place a file at the target location
        fileSystem.AddDirectory(DataDirectory + "TestSeries/");
        fileSystem.AddFile(DataDirectory + "TestSeries/test.cbz", new MockFileData("existing"));

        var service = CreateService(unitOfWork, fileSystem);

        var dto = new ConfirmUploadDto
        {
            LibraryId = library.Id,
            Files = new List<ConfirmUploadFileDto>
            {
                new()
                {
                    TempFileName = tempFileName,
                    OriginalFileName = "test.cbz",
                    Series = "TestSeries",
                    Volume = "",
                    Number = ""
                }
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmUploadsAsync(dto, user.Id));
    }

    #endregion

    #region ReEnrichAsync

    [Fact]
    public async Task ReEnrichAsync_WithSuccessfulEnrichment_ReturnsPopulatedResult()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var enrichmentService = Substitute.For<IMetadataEnrichmentService>();
        enrichmentService.EnrichAsync(Arg.Any<EnrichmentContext>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(new EnrichmentResult
            {
                Success = true,
                Source = MetadataSource.ComicVine,
                Series = "Batman",
                Title = "The Dark Knight",
                Writer = "Frank Miller",
                Summary = "A dark tale",
                Publisher = "DC Comics",
                Genre = "Action,Superhero",
                Year = 1986,
                ExternalUrl = "https://comicvine.example.com/batman"
            });

        var service = CreateService(unitOfWork, enrichmentService: enrichmentService);

        var result = await service.ReEnrichAsync("Batman", MangaFormat.Archive);

        Assert.True(result.Success);
        Assert.Equal((int)MetadataSource.ComicVine, result.MetadataSource);
        Assert.Equal("Batman", result.Series);
        Assert.Equal("The Dark Knight", result.Title);
        Assert.Equal("Frank Miller", result.Writer);
        Assert.Equal("A dark tale", result.Summary);
        Assert.Equal("DC Comics", result.Publisher);
        Assert.Equal("Action,Superhero", result.Genre);
        Assert.Equal(1986, result.Year);
        Assert.Equal("https://comicvine.example.com/batman", result.ExternalUrl);
    }

    [Fact]
    public async Task ReEnrichAsync_WithNoResults_ReturnsUnsuccessful()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var service = CreateService(unitOfWork);

        var result = await service.ReEnrichAsync("NonexistentSeries", MangaFormat.Archive);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ReEnrichAsync_WithEnrichmentException_ReturnsUnsuccessful()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var enrichmentService = Substitute.For<IMetadataEnrichmentService>();
        enrichmentService.EnrichAsync(Arg.Any<EnrichmentContext>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns<EnrichmentResult>(_ => throw new Exception("Provider down"));

        var service = CreateService(unitOfWork, enrichmentService: enrichmentService);

        var result = await service.ReEnrichAsync("Batman", MangaFormat.Archive);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ReEnrichAsync_PassesCorrectContextToEnrichmentService()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var enrichmentService = Substitute.For<IMetadataEnrichmentService>();
        enrichmentService.EnrichAsync(Arg.Any<EnrichmentContext>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(new EnrichmentResult { Success = false, Source = MetadataSource.Local });

        var service = CreateService(unitOfWork, enrichmentService: enrichmentService);

        await service.ReEnrichAsync("One Piece", MangaFormat.Epub);

        await enrichmentService.Received(1).EnrichAsync(
            Arg.Is<EnrichmentContext>(ctx =>
                ctx.Series == "One Piece" &&
                ctx.Format == MangaFormat.Epub &&
                ctx.Title == string.Empty &&
                ctx.Writer == string.Empty &&
                ctx.Volume == string.Empty),
            Arg.Any<System.Threading.CancellationToken>());
    }

    [Fact]
    public async Task ReEnrichAsync_PassesNumberIsbnAndSourceToEnrichmentService()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var enrichmentService = Substitute.For<IMetadataEnrichmentService>();
        enrichmentService.EnrichAsync(Arg.Any<EnrichmentContext>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(new EnrichmentResult { Success = false, Source = MetadataSource.Local });

        var service = CreateService(unitOfWork, enrichmentService: enrichmentService);

        await service.ReEnrichAsync("Batman", MangaFormat.Archive, number: "5", isbn: "978-3-16-148410-0", source: MetadataSource.ComicVine);

        await enrichmentService.Received(1).EnrichAsync(
            Arg.Is<EnrichmentContext>(ctx =>
                ctx.Series == "Batman" &&
                ctx.Format == MangaFormat.Archive &&
                ctx.Number == "5" &&
                ctx.Isbn == "978-3-16-148410-0" &&
                ctx.PreferredSource == MetadataSource.ComicVine),
            Arg.Any<System.Threading.CancellationToken>());
    }

    #endregion

    #region SearchEnrichAsync

    [Fact]
    public async Task SearchEnrichAsync_ReturnsMultipleResults()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var enrichmentService = Substitute.For<IMetadataEnrichmentService>();
        enrichmentService.SearchAsync(Arg.Any<EnrichmentContext>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(new List<EnrichmentResult>
            {
                new()
                {
                    Success = true,
                    Source = MetadataSource.ComicVine,
                    MatchScore = 0.95,
                    Series = "Battle Angel Alita",
                    Writer = "Yukito Kishiro",
                    Summary = "A cyborg story",
                    Publisher = "Viz Media",
                    Year = 1990,
                    ExternalUrl = "https://comicvine.example.com/alita"
                },
                new()
                {
                    Success = true,
                    Source = MetadataSource.ComicVine,
                    MatchScore = 0.70,
                    Series = "Battle Angel Alita: Last Order",
                    Writer = "Yukito Kishiro",
                    Summary = "The sequel",
                    Publisher = "Viz Media",
                    Year = 2000,
                    ExternalUrl = "https://comicvine.example.com/alita-lo"
                }
            });

        var service = CreateService(unitOfWork, enrichmentService: enrichmentService);

        var result = await service.SearchEnrichAsync("Battle Angel", MangaFormat.Archive);

        Assert.True(result.Success);
        Assert.Equal(2, result.Results.Count);
        Assert.Equal("Battle Angel Alita", result.Results[0].Series);
        Assert.Equal(0.95, result.Results[0].MatchScore);
        Assert.Equal((int)MetadataSource.ComicVine, result.Results[0].MetadataSource);
        Assert.Equal("Battle Angel Alita: Last Order", result.Results[1].Series);
    }

    [Fact]
    public async Task SearchEnrichAsync_NoResults_ReturnsSuccessFalse()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var enrichmentService = Substitute.For<IMetadataEnrichmentService>();
        enrichmentService.SearchAsync(Arg.Any<EnrichmentContext>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(new List<EnrichmentResult>());

        var service = CreateService(unitOfWork, enrichmentService: enrichmentService);

        var result = await service.SearchEnrichAsync("NonexistentSeries", MangaFormat.Archive);

        Assert.False(result.Success);
        Assert.Empty(result.Results);
    }

    #endregion
}
