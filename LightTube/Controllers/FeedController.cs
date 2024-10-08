using System.Text;
using System.Web;
using System.Xml;
using InnerTube;
using InnerTube.Models;
using LightTube.Contexts;
using LightTube.Database;
using LightTube.Database.Models;
using LightTube.Localization;
using Microsoft.AspNetCore.Mvc;

namespace LightTube.Controllers;

[Route("/feed")]
public class FeedController(SimpleInnerTubeClient innerTube) : Controller
{
    
    [Route("channel/{c}.xml")]
    [HttpGet]
    public async Task<IActionResult> ChannelFeed(string c)
    {
        ChannelFeed ytchannel = await YoutubeRss.GetChannelFeed(c);
        XmlDocument document = new();
        XmlElement rss = document.CreateElement("rss");
        rss.SetAttribute("version", "2.0");

        XmlElement channel = document.CreateElement("channel");

        XmlElement title = document.CreateElement("title");
        title.InnerText = ytchannel.Name;
        channel.AppendChild(title);

        XmlElement description = document.CreateElement("description");
        description.InnerText = $"LightTube channel RSS feed for {ytchannel.Name}";
        channel.AppendChild(description);

        foreach (FeedVideo video in ytchannel.Videos.Take(15))
        {
            XmlElement item = document.CreateElement("item");

            XmlElement id = document.CreateElement("id");
            id.InnerText = $"id:video:{video.Id}";
            item.AppendChild(id);

            XmlElement vtitle = document.CreateElement("title");
            vtitle.InnerText = video.Title;
            item.AppendChild(vtitle);

            XmlElement vdescription = document.CreateElement("description");
            vdescription.InnerText = video.Description;
            item.AppendChild(vdescription);

            XmlElement link = document.CreateElement("link");
            link.InnerText = $"https://{Request.Host}/watch?v={video.Id}";
            item.AppendChild(link);

            XmlElement published = document.CreateElement("pubDate");
            published.InnerText = video.PublishedDate.ToString("R");
            item.AppendChild(published);

            XmlElement author = document.CreateElement("author");

            XmlElement name = document.CreateElement("name");
            name.InnerText = video.ChannelName;
            author.AppendChild(name);

            XmlElement uri = document.CreateElement("uri");
            uri.InnerText = $"https://{Request.Host}/channel/{video.ChannelId}";
            author.AppendChild(uri);

            item.AppendChild(author);
            channel.AppendChild(item);
        }

        rss.AppendChild(channel);

        document.AppendChild(rss);
        return File(Encoding.UTF8.GetBytes(document.OuterXml), "application/xml");
    }

    [Route("subscriptions")]
    public async Task<IActionResult> Subscription()
    {
        SubscriptionsContext ctx = new(HttpContext);
        if (ctx.User is null) return Redirect("/account/login?redirectUrl=%2ffeed%2fsubscriptions");
        ctx.Videos = await YoutubeRss.GetMultipleFeeds(ctx.User.Subscriptions.Keys);
        return View(ctx);
    }

    [Route("rss.xml")]
    public async Task<IActionResult> RssFeed()
    {
        if (string.IsNullOrWhiteSpace(Request.Headers.Authorization))
        {
            Response.Headers.Add("WWW-Authenticate", "Basic realm=\"Access to the personalized RSS feed\"");
            return Unauthorized();
        }

        try
        {
            string type = Request.Headers.Authorization.First().Split(' ').First();
            string secret = Request.Headers.Authorization.First().Split(' ').Last();
            string secretDecoded = Encoding.UTF8.GetString(Convert.FromBase64String(secret));
            string username = secretDecoded.Split(':')[0];
            string password = secretDecoded.Split(':')[1];
            DatabaseUser? user = await DatabaseManager.Users.GetUserFromUsernamePassword(username, password) ?? throw new Exception();
            FeedVideo[] feedVideos = await YoutubeRss.GetMultipleFeeds(user.Subscriptions.Where(x => x.Value == SubscriptionType.NOTIFICATIONS_ON).Select(x => x.Key));

            XmlDocument document = new();
            XmlElement rss = document.CreateElement("rss");
            rss.SetAttribute("version", "2.0");

            XmlElement channel = document.CreateElement("channel");

            XmlElement title = document.CreateElement("title");
            title.InnerText = "LightTube subscriptions RSS feed for " + user.UserID;
            channel.AppendChild(title);

            XmlElement description = document.CreateElement("description");
            description.InnerText = $"LightTube subscriptions RSS feed for {user.UserID} with {user.Subscriptions.Count} channels";
            channel.AppendChild(description);

            foreach (FeedVideo video in feedVideos.Take(15))
            {
                XmlElement item = document.CreateElement("item");

                XmlElement id = document.CreateElement("id");
                id.InnerText = $"id:video:{video.Id}";
                item.AppendChild(id);

                XmlElement vtitle = document.CreateElement("title");
                vtitle.InnerText = video.Title;
                item.AppendChild(vtitle);

                XmlElement vdescription = document.CreateElement("description");
                vdescription.InnerText = video.Description;
                item.AppendChild(vdescription);

                XmlElement link = document.CreateElement("link");
                link.InnerText = $"https://{Request.Host}/watch?v={video.Id}";
                item.AppendChild(link);

                XmlElement published = document.CreateElement("pubDate");
                published.InnerText = video.PublishedDate.ToString("R");
                item.AppendChild(published);

                XmlElement author = document.CreateElement("author");

                XmlElement name = document.CreateElement("name");
                name.InnerText = video.ChannelName;
                author.AppendChild(name);

                XmlElement uri = document.CreateElement("uri");
                uri.InnerText = $"https://{Request.Host}/channel/{video.ChannelId}";
                author.AppendChild(uri);

                item.AppendChild(author);
                channel.AppendChild(item);
            }

            rss.AppendChild(channel);

            document.AppendChild(rss);
            return File(Encoding.UTF8.GetBytes(document.OuterXml), "application/xml");
        }
        catch (Exception e)
        {
            Response.Headers.Add("WWW-Authenticate", "Basic realm=\"Access to the personalized RSS feed\"");
            return Unauthorized();
        }
    }

    [Route("channels")]
    [HttpGet]
    public IActionResult Channels()
    {
        ChannelsContext ctx = new(HttpContext);
        if (ctx.User is null) return Redirect("/account/login?redirectUrl=%2ffeed%2fchannels");
        ctx.Channels = from v in ctx.User.Subscriptions.Keys
            .Select(DatabaseManager.Cache.GetChannel)
            .Where(x => x is not null)
                       orderby v.Name
                       select v;
        return View(ctx);
    }

    [Route("channels")]
    [HttpPost]
    public async Task<IActionResult> ChannelsAsync([FromForm] Dictionary<string, string> data)
    {
        BaseContext c = new(HttpContext);
        if (c.User is null) return Redirect("/account/login?redirectUrl=%2ffeed%2fchannels");
        foreach ((string id, string type) in data)
        {
            if (int.TryParse(type, out int subTypeInt))
            {
                SubscriptionType newType = (SubscriptionType)subTypeInt;
                if (c.User.Subscriptions.TryGetValue(id, out SubscriptionType oldType))
                    if (newType != oldType)
                    {
                        await DatabaseManager.Users.UpdateSubscription(Request.Cookies["token"] ?? "", id, newType);
                    }
                    else
                        await DatabaseManager.Users.UpdateSubscription(Request.Cookies["token"] ?? "", id, newType);
            }
        }

        ChannelsContext ctx = new(HttpContext);
        ctx.Channels = from v in ctx.User!.Subscriptions.Keys
            .Select(DatabaseManager.Cache.GetChannel)
            .Where(x => x is not null)
                       orderby v.Name
                       select v;
        return View(ctx);
    }

    [Route("library")]
    [HttpGet]
    public async Task<IActionResult> Library()
    {
        LibraryContext c = new(HttpContext);
        if (c.User is null) return Redirect("/account/login?redirectUrl=%2ffeed%2flibrary");
        return View(c);
    }

    [Route("/newPlaylist")]
    [HttpGet]
    public IActionResult NewPlaylist(string? v)
    {
        ModalContext ctx = new(HttpContext);
        if (ctx.User is null) return Redirect("/account/login?redirectUrl=" + HttpUtility.UrlEncode(Request.Path + Request.Query));
        ctx.Buttons =
        [
            new ModalButton(v ?? "", "|", ""),
            new ModalButton(ctx.Localization.GetRawString("playlist.create.confirm"), "__submit", "primary"),
        ];
        ctx.Title = ctx.Localization.GetRawString("playlist.create.title");
        ctx.AlignToStart = true;
        return View(ctx);
    }

    [Route("/newPlaylist")]
    [HttpPost]
    public async Task<IActionResult> NewPlaylist(string title, string description, PlaylistVisibility visibility, string firstVideo)
    {
        try
        {
            DatabasePlaylist pl = await DatabaseManager.Playlists.CreatePlaylist(Request.Cookies["token"], title, description, visibility);
            if (firstVideo != null)
            {
                InnerTubePlayer v = await innerTube.GetVideoPlayerAsync(firstVideo, true,
                    HttpContext.GetInnerTubeLanguage(), HttpContext.GetInnerTubeRegion());
                await DatabaseManager.Playlists.AddVideoToPlaylist(Request.Cookies["token"], pl.Id, v);
            }
            return Ok(LocalizationManager.GetFromHttpContext(HttpContext).GetRawString("modal.close"));
        }
        catch (Exception e)
        {
            return Ok(e.Message);
        }
    }

    [Route("/editPlaylist")]
    [HttpGet]
    public IActionResult EditPlaylist(string id)
    {
        PlaylistVideoContext<IEnumerable<DatabasePlaylist>> ctx = new(HttpContext);
        if (ctx.User is null) return Redirect("/account/login?redirectUrl=" + HttpUtility.UrlEncode(Request.Path + Request.Query));

        DatabasePlaylist? playlist = DatabaseManager.Playlists.GetPlaylist(id);
        if (playlist is null) return Redirect("/");

        if (playlist.Author != ctx.User.UserID) return Redirect("/");

        ctx.ItemId = playlist.Id;
        ctx.ItemTitle = playlist.Name;
        ctx.ItemSubtitle = playlist.Description;
        ctx.ItemThumbnail = ((int)playlist.Visibility).ToString();

        ctx.Buttons =
        [
            new ModalButton("", "|", ""),
            new ModalButton(ctx.Localization.GetRawString("playlist.edit.confirm"), "__submit", "primary"),
        ];
        ctx.Title = ctx.Localization.GetRawString("playlist.edit.title");
        ctx.AlignToStart = true;
        return View(ctx);
    }

    [Route("/editPlaylist")]
    [HttpPost]
    public async Task<IActionResult> EditPlaylist(string id, string title, string description, PlaylistVisibility visibility)
    {
        try
        {
            await DatabaseManager.Playlists.EditPlaylist(Request.Cookies["token"], id, title, description, visibility);
            return Ok(LocalizationManager.GetFromHttpContext(HttpContext).GetRawString("modal.close"));
        }
        catch (Exception e)
        {
            return Ok(e.Message);
        }
    }

    [Route("/deletePlaylist")]
    [HttpGet]
    public IActionResult DeletePlaylist(string id)
    {
        PlaylistVideoContext<IEnumerable<DatabasePlaylist>> ctx = new(HttpContext);
        if (ctx.User is null) return Redirect("/account/login?redirectUrl=" + HttpUtility.UrlEncode(Request.Path + Request.Query));

        DatabasePlaylist? playlist = DatabaseManager.Playlists.GetPlaylist(id);
        if (playlist is null) return Redirect("/");

        if (playlist.Author != ctx.User.UserID) return Redirect("/");

        ctx.ItemId = playlist.Id;
        ctx.ItemTitle = playlist.Name;
        ctx.ItemSubtitle = string.Format(ctx.Localization.GetRawString("playlist.videos.count"), playlist.VideoIds.Count);
        ctx.ItemThumbnail = $"https://i.ytimg.com/vi/{playlist.VideoIds.FirstOrDefault()}/hqdefault.jpg";

        ctx.Buttons =
        [
            new ModalButton("", "|", ""),
            new ModalButton(ctx.Localization.GetRawString("playlist.delete.confirm"), "__submit", "primary"),
        ];
        ctx.Title = ctx.Localization.GetRawString("playlist.delete.title");
        return View(ctx);
    }

    [Route("/deletePlaylist")]
    [HttpPost]
    public async Task<IActionResult> DeletePlaylist(string id, string title, string description, PlaylistVisibility visibility)
    {
        try
        {
            await DatabaseManager.Playlists.DeletePlaylist(Request.Cookies["token"], id);
            return Ok(LocalizationManager.GetFromHttpContext(HttpContext).GetRawString("modal.close"));
        }
        catch (Exception e)
        {
            return Ok(e.Message);
        }
    }

    [HttpGet]
    [Route("/addToPlaylist")]
    public async Task<IActionResult> AddToPlaylist(string v)
    {
        InnerTubeVideo videoDetails = await innerTube.GetVideoDetailsAsync(v, true, null, null, null,
            HttpContext.GetInnerTubeLanguage(), HttpContext.GetInnerTubeRegion());
        PlaylistVideoContext<IEnumerable<DatabasePlaylist>> pvc = new(HttpContext, videoDetails);
        if (pvc.User is null) return Redirect("/account/login?redirectUrl=" + HttpUtility.UrlEncode(Request.Path + Request.Query));
        pvc.Extra = DatabaseManager.Playlists.GetUserPlaylists(pvc.User.UserID, PlaylistVisibility.Private);
        pvc.Buttons =
        [
            new ModalButton("", "|", ""),
            new ModalButton(pvc.Localization.GetRawString("playlist.add.confirm"), "__submit", "primary"),
        ];
        pvc.Title = pvc.Localization.GetRawString("playlist.add.title");
        return View(pvc);
    }

    [Route("/addToPlaylist")]
    [HttpPost]
    public async Task<IActionResult> AddToPlaylist(string playlist, string video)
    {
        try
        {
            if (playlist == "__NEW") return Redirect("/newPlaylist?v=" + video);

            InnerTubePlayer v = await innerTube.GetVideoPlayerAsync(video, true, HttpContext.GetInnerTubeLanguage(),
                HttpContext.GetInnerTubeRegion());
            await DatabaseManager.Playlists.AddVideoToPlaylist(Request.Cookies["token"], playlist, v);
            return Ok(LocalizationManager.GetFromHttpContext(HttpContext).GetRawString("modal.close"));
        }
        catch (Exception e)
        {
            return Ok(e.Message);
        }
    }

    [HttpGet]
    [Route("/removeFromPlaylist")]
    public async Task<IActionResult> RemoveFromPlaylist(string v, string list)
    {
        DatabaseVideo? video = DatabaseManager.Cache.GetVideo(v[..11]);

        PlaylistVideoContext<IEnumerable<DatabasePlaylist>> pvc = new(HttpContext, video, v);
        if (pvc.User is null) return Redirect("/account/login?redirectUrl=" + HttpUtility.UrlEncode(Request.Path + Request.Query));

        DatabasePlaylist? playlist = DatabaseManager.Playlists.GetPlaylist(list);
        if (playlist is null) return Redirect("/");

        if (playlist.Author != pvc.User.UserID) return Redirect("/");

        pvc.Buttons =
        [
            new ModalButton(playlist.Name, "|", playlist.Id),
            new ModalButton(pvc.Localization.GetRawString("playlist.removevideo.confirm"), "__submit", "primary"),
        ];
        pvc.Title = pvc.Localization.GetRawString("playlist.removevideo.title");
        return View(pvc);
    }

    [Route("/removeFromPlaylist")]
    [HttpPost]
    public async Task<IActionResult> RemoveFromPlaylist(string playlist, string video, string __RequestVerificationToken)
    {
        try
        {
            await DatabaseManager.Playlists.RemoveVideoFromPlaylist(Request.Cookies["token"], playlist, video);
            return Ok(LocalizationManager.GetFromHttpContext(HttpContext).GetRawString("modal.close"));
        }
        catch (Exception e)
        {
            return Ok(e.Message);
        }
    }
}