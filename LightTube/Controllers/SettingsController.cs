using System.Diagnostics;
using InnerTube;
using InnerTube.Models;
using LightTube.ApiModels;
using LightTube.Contexts;
using LightTube.Database;
using LightTube.Database.Models;
using LightTube.Localization;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace LightTube.Controllers;

[Route("/settings")]
public class SettingsController(SimpleInnerTubeClient innerTube) : Controller
{
    [Route("/settings")]
    public IActionResult Settings() => RedirectPermanent("/settings/appearance");

    [Route("content")]
    public IActionResult Content() => RedirectPermanent("/settings/appearance");

    [Route("appearance")]
    [HttpGet]
    public IActionResult Appearance()
    {
        ApiLocals locals = Utils.GetLocals();
        AppearanceSettingsContext ctx = new(HttpContext, locals, Configuration.CustomThemeDefs, LocalizationManager.GetAllLanguages(), LocalizationManager.GetLanguagePercentages());
        return View(ctx);
    }

    [Route("appearance")]
    [HttpPost]
    public IActionResult Appearance(string hl, string gl, string theme, string recommendations, string compatibility,
        string maxvideos, string language)
    {
        Response.Cookies.Append("hl", hl, new CookieOptions
        {
            Expires = DateTimeOffset.MaxValue
        });
        Response.Cookies.Append("gl", gl, new CookieOptions
        {
            Expires = DateTimeOffset.MaxValue
        });
        Response.Cookies.Append("theme", theme, new CookieOptions
        {
            Expires = DateTimeOffset.MaxValue
        });
        Response.Cookies.Append("recommendations", recommendations == "on" ? "visible" : "collapsed", new CookieOptions
        {
            Expires = DateTimeOffset.MaxValue
        });
        Response.Cookies.Append("compatibility", compatibility == "on" ? "true" : "false", new CookieOptions
        {
            Expires = DateTimeOffset.MaxValue
        });
        Response.Cookies.Append("maxvideos", maxvideos, new CookieOptions
        {
            Expires = DateTimeOffset.MaxValue
        });
        if (language != LocalizationManager.GetFromHttpContext(HttpContext).CurrentLocale)
            Response.Cookies.Append("languageOverride", language, new CookieOptions
            {
                Expires = DateTimeOffset.MaxValue
            });
        return Redirect("/settings/appearance");
    }

    [Route("account")]
    public IActionResult Account()
    {
        BaseContext context = new(HttpContext);
        if (context.User == null) return Redirect("/account/login?redirectUrl=%2fsettings%2fdata");

        return View(new BaseContext(HttpContext));
    }

    [Route("data")]
    [HttpGet]
    [HttpPost]
    public IActionResult ImportExport()
    {
        BaseContext context = new(HttpContext);
        if (context.User == null) return Redirect("/account/login?redirectUrl=%2fsettings%2fdata");

        if (Request.Method != "POST") return View(new ImportContext(HttpContext));

        try
        {
            IFormFile file = Request.Form.Files[0];
            if (file.Length is 0 or > 10 * 1024 * 1024)
                return View(new ImportContext(HttpContext, "Imported file cannot be larger than 10 megabytes.", true));
            using Stream fileStream = file.OpenReadStream();
            using MemoryStream memStr = new();
            fileStream.CopyTo(memStr);
            byte[] bytes = memStr.ToArray();
            ImportedData importedData = ImporterUtility.ExtractData(bytes);
            string[] channelIds = importedData.Subscriptions.Select(x => x.Id).Distinct().ToArray();
            ImportedData.Playlist[] playlists = [.. importedData.Playlists];
            string[] videos = importedData.Playlists.SelectMany(x => x.VideoIds).Distinct().ToArray();
            memStr.Dispose();
            fileStream.Dispose();

            string token = Request.Cookies["token"] ?? "";

            // import channels
            Task.Run(() =>
            {
                foreach (string[] ids in channelIds.Chunk(50))
                {
                    Stopwatch sp = Stopwatch.StartNew();
                    Task[] channelTasks = ids.Select(id => Task.Run(async () =>
                        {
                            try
                            {
                                await DatabaseManager.Users.UpdateSubscription(token, id,
                                    SubscriptionType.NOTIFICATIONS_ON);
                            }
                            catch (Exception)
                            {
                                // simply ignore 😇
                            }
                        }))
                        .ToArray();

                    try
                    {
                        Task.WaitAll(channelTasks, TimeSpan.FromSeconds(30));
                        sp.Stop();
                        Log.Debug(
                            $"Subscribed to {channelTasks.Length} more channels in {sp.Elapsed}.");
                    }
                    catch (Exception)
                    {
                        // simply ignore 😇
                    }
                }
            });

            // import playlists
            Task.Run(async () =>
            {
                Dictionary<string, InnerTubePlayer> videoNexts = [];
                foreach (string[] videoIds in videos.Chunk(100))
                {
                    Stopwatch sp = Stopwatch.StartNew();
                    Task[] videoTasks = videoIds.Select(id => Task.Run(async () =>
                        {
                            try
                            {
                                InnerTubePlayer video = await innerTube.GetVideoPlayerAsync(id, true);
                                videoNexts.Add(id, video);
                            }
                            catch (Exception e)
                            {
                                Log.Error("error/video: " + e.Message);
                            }
                        }))
                        .ToArray();

                    try
                    {
                        Task.WaitAll(videoTasks, TimeSpan.FromSeconds(30));
                        sp.Stop();
                        Log.Debug(
                            $"Got {videoTasks.Length} more videos in {sp.Elapsed}. {videoNexts.Count} success");
                    }
                    catch (Exception e)
                    {
                        Log.Error("Error while getting videos\n" + e);
                    }
                }

                Log.Debug(
                    $"From {videos.Length} videos, got {videoNexts.Count} videos.");

                foreach (ImportedData.Playlist playlist in playlists)
                {
                    DatabasePlaylist pl = await DatabaseManager.Playlists.CreatePlaylist(token, playlist.Title,
                        playlist.Description, playlist.Visibility);
                    foreach (string video in playlist.VideoIds)
                    {
                        if (!videoNexts.TryGetValue(video, out InnerTubePlayer? next)) continue;
                        await DatabaseManager.Playlists.AddVideoToPlaylist(token, pl.Id,
                            next);
                    }
                }
            });

            return View(new ImportContext(HttpContext,
                $"Import process started. It might take a few minutes for all the content to appear on your account\n{channelIds.Length} channels, {playlists.Length} playlists, {videos.Length} videos"));
        }
        catch (Exception e)
        {
            return View(new ImportContext(HttpContext, e.Message, true));
        }
    }
}
