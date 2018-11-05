using IdentityModel.OidcClient;
using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ConsoleClientWithBrowser
{
    public class Program
    {
        //For production, this should be set to https://id.qedi.co.uk/
        private static string _authority = "";
        private static string _clientId = "";

        private const string LEVEL_HEADER = "X-GoTechnology-Level";
        static OidcClient _oidcClient;
        static HttpClient _apiClient = new HttpClient();

        private static Guid _selectedLevelEId = Guid.Empty;

        public static void Main(string[] args) => MainAsync().GetAwaiter().GetResult();

        public static async Task MainAsync()
        {
            Console.WriteLine("+-------------------------------+");
            Console.WriteLine("|  Sign in to qedi with OIDC    |");
            Console.WriteLine("+-------------------------------+");
            Console.WriteLine("");
            Console.WriteLine("Press any key to sign in...");
            Console.ReadKey();

            await SignIn();
        }

        private static async Task SignIn()
        {
            // create a redirect URI using an available port on the loopback address.
            // requires the OP to allow random ports on 127.0.0.1 - otherwise set a static port
            var browser = new SystemBrowser(33488);
            string redirectUri = string.Format($"http://127.0.0.1:{browser.Port}");

            var options = new OidcClientOptions
            {
                Authority = _authority,
                ClientId = _clientId,
                RedirectUri = redirectUri,
                Scope = "openid profile email extended_profile hub2_api offline_access",
                FilterClaims = false,
                Browser = browser
            };

            var serilog = new LoggerConfiguration()
                .MinimumLevel.Error()
                .Enrich.FromLogContext()
                .WriteTo.LiterateConsole(outputTemplate: "[{Timestamp:HH:mm:ss} {Level}] {SourceContext}{NewLine}{Message}{NewLine}{Exception}{NewLine}")
                .CreateLogger();

            options.LoggerFactory.AddSerilog(serilog);

            _oidcClient = new OidcClient(options);
            var result = await _oidcClient.LoginAsync(new LoginRequest());

            ShowResult(result);
            await NextSteps(result);
        }

        private static void ShowResult(LoginResult result)
        {
            if (result.IsError)
            {
                Console.WriteLine("\n\nError:\n{0}", result.Error);
                return;
            }

            Console.WriteLine("\n\nClaims:");
            foreach (var claim in result.User.Claims)
            {
                Console.WriteLine("{0}: {1}", claim.Type, claim.Value);
            }

            //We can access any claims we need off the Principle in the result
            var email = result.User.Claims.FirstOrDefault(c => c.Type == "email").Value;
            var firstName = result.User.Claims.FirstOrDefault(c => c.Type == "given_name").Value;
            var lastName = result.User.Claims.FirstOrDefault(c => c.Type == "family_name").Value;
            var dateLocale = result.User.Claims.FirstOrDefault(c => c.Type == "date_locale").Value;

            //We could show a picker to the user if they have access to more than one instance of hub2
            var instancesJson = result.User.Claims.FirstOrDefault(c => c.Type == "hub2_instances");

            //Extract the list of instances from the API response, and set the API base to Alpha for now
            var instances = JsonConvert.DeserializeObject<List<UserInstanceApiDto>>(instancesJson.Value);
            var baseUri = instances.First(i => i.Name.Contains("Alpha")).Url;
            _apiClient.BaseAddress = new Uri(baseUri);

            Console.WriteLine($"\nidentity token: {result.IdentityToken}");
            Console.WriteLine($"access token:   {result.AccessToken}");
            Console.WriteLine($"refresh token:  {result?.RefreshToken ?? "none"}");
        }

        private static async Task NextSteps(LoginResult result)
        {
            var currentAccessToken = result.AccessToken;
            var currentRefreshToken = result.RefreshToken;

            var menu = "  x...exit  l...get levels  d...query disciplines  ";
            if (currentRefreshToken != null) menu += "r...refresh token   ";

            while (true)
            {
                Console.WriteLine("\n\n");

                Console.Write(menu);
                var key = Console.ReadKey();

                if (key.Key == ConsoleKey.X) return;
                if (key.Key == ConsoleKey.L) await GetLevels(currentAccessToken);
                if (key.Key == ConsoleKey.D) await QueryDisciplines(currentAccessToken);
                if (key.Key == ConsoleKey.R)
                {
                    var refreshResult = await _oidcClient.RefreshTokenAsync(currentRefreshToken);
                    if (refreshResult.IsError)
                    {
                        Console.WriteLine($"Error: {refreshResult.Error}");
                    }
                    else
                    {
                        currentRefreshToken = refreshResult.RefreshToken;
                        currentAccessToken = refreshResult.AccessToken;

                        Console.WriteLine("\n\n");
                        Console.WriteLine($"access token:   {currentAccessToken}");
                        Console.WriteLine($"refresh token:  {currentRefreshToken ?? "none"}");
                    }
                }
            }
        }

        private static async Task GetLevels(string currentAccessToken)
        {
            _apiClient.SetBearerToken(currentAccessToken);
            var response = await _apiClient.GetAsync("api/Level/User");

            if (response.IsSuccessStatusCode)
            {
                var json = JArray.Parse(await response.Content.ReadAsStringAsync());
                Console.WriteLine("\n\n");
                Console.WriteLine(json);

                //Extract the levels from the API response
                var levelAs = json.ToObject<List<LevelApiDto>>();

                //We should show a picker to the user if they have access to more than one Level E
                var levelEs = new List<LevelApiDto>();
                foreach (var levelA in levelAs)
                {
                    FindLevelEs(levelA, levelEs);
                }

                //For demo purposes, just set the ID of the first Level E as the 'Selected' one
                _selectedLevelEId = levelEs.First().Id;
            }
            else
            {
                Console.WriteLine($"Error: {response.ReasonPhrase}");
            }
        }

        private static async Task QueryDisciplines(string currentAccessToken)
        {
            _apiClient.SetBearerToken(currentAccessToken);

            //Add the Level Header based on the 'Selected' Level
            if (!_apiClient.DefaultRequestHeaders.Contains(LEVEL_HEADER))
            {
                _apiClient.DefaultRequestHeaders.Add(LEVEL_HEADER, _selectedLevelEId.ToString());
            }

            //For example, query some disciplines via the API
            var response = await _apiClient.GetAsync("api/Discipline");

            if (response.IsSuccessStatusCode)
            {
                var json = JArray.Parse(await response.Content.ReadAsStringAsync());
                Console.WriteLine("\n\n");
                Console.WriteLine(json);
            }
            else
            {
                Console.WriteLine($"Error: {response.ReasonPhrase}");
            }
        }

        private class LevelApiDto
        {
            public Guid Id { get; set; }
            public string Name { get; set; }
            public IEnumerable<LevelApiDto> Children { get; set; }
        }

        private class UserInstanceApiDto
        {
            public string Name { get; set; }
            public string Url { get; set; }
            public string Description { get; set; }
            public DateTime LastAccessed { get; set; }
        }

        /// <summary>
        /// Recursively find the level Es in the tree
        /// </summary>
        private static void FindLevelEs(LevelApiDto p, ICollection<LevelApiDto> leaves)
        {
            if (p.Children != null)
                foreach (var child in p.Children)
                {
                    if (child.Children == null)
                        leaves.Add(child);
                    FindLevelEs(child, leaves);
                }
        }
    }
}
