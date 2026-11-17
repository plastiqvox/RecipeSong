using System.Net;
using RecipeSong.Core;
using RecipeSong.Core.Providers;

/// <summary>디스코그래피를 받지 못한 것을 빈 디스코그래피로 캐시하지 않는다 (할당량 초과로 신보가 3일 동안 통째로 빠졌다)</summary>
static class DiscographyCacheChecks
{
    public static async Task RunAsync()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var handler = new SwitchHandler();
        using var http = new HttpClient(handler);
        var engine = new ReleaseRecommender(new DeezerClient(http));

        handler.Body = null;
        Check((await engine.FollowedDiscographyAsync(7)).Count == 0, "실패한 요청에서 앨범이 나옴");
        handler.Body = """{"data":[{"id":1,"title":"Album","release_date":"2026-11-13","record_type":"album"}]}""";
        Check((await engine.FollowedDiscographyAsync(7)).Count == 1, "받지 못한 디스코그래피가 빈 목록으로 캐시됨");
        handler.Body = null;
        Check((await engine.FollowedDiscographyAsync(7)).Count == 1 && handler.Calls == 2, "캐시된 디스코그래피를 다시 요청함");

        handler.Body = """{"data":[]}""";
        Check((await engine.FollowedDiscographyAsync(8)).Count == 0, "빈 디스코그래피");
        handler.Body = """{"data":[{"id":2,"title":"Later","release_date":"2026-11-13","record_type":"single"}]}""";
        Check((await engine.FollowedDiscographyAsync(8)).Count == 1, "예전에 저장된 빈 목록을 다시 받지 않음");

        Console.WriteLine("디스코그래피 캐시 검사 통과");
    }

    sealed class SwitchHandler : HttpMessageHandler
    {
        public string? Body;
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Body == null ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body) });
        }
    }
}
