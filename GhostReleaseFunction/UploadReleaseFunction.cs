using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Newtonsoft.Json;

namespace GhostVersionFunctionApp
{
    public static class UploadReleaseFunction
    {
        public static string GitUserName { get; set; } = GetEnvironmentVariable("GitUserName");
        public static string GitPassword { get; set; } = GetEnvironmentVariable("GitPassword");

        public static string GitRepoOwner { get; set; } = GetEnvironmentVariable("GitRepoOwner");
        public static string GitRepoName { get; set; } = GetEnvironmentVariable("GitRepoName");
        public static string GitRepoBranch { get; set; } = GetEnvironmentVariable("GitRepoBranch");

        public static string GitAuthorName { get; set; } = GetEnvironmentVariable("GitAuthorName");
        public static string GitAuthorEmail { get; set; } = GetEnvironmentVariable("GitAuthorEmail");

        public static CredentialsHandler Handler => (_url, _user, _cred) =>
            new UsernamePasswordCredentials { Username = GitUserName, Password = GitPassword };

        private static string GetEnvironmentVariable(string name)
        {
            return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
        }

        private static async Task CreateRelease(string releaseName, string releaseNotes)
        {
            var data = new
            {
                tag_name = releaseName,
                target_commitish = GitRepoBranch,
                name = releaseName,
                body = releaseNotes,
                draft = false,
                prerelease = false
            };
            var stringContent = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");

            using (HttpClient hc = new HttpClient())
            {
                // You must set a user agent so that the CRLF requirement on the header parsing is met.
                // Otherwise you will get an excpetion message with "The server committed a protocol violation. Section=ResponseStatusLine"
                hc.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("Mozilla", "5.0"));
                await hc.PostAsync($"https://api.github.com/repos/{GitRepoOwner}/{GitRepoName}/releases?access_token={GitPassword}", stringContent);
            }
        }

        [FunctionName("ghost-release")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Function, "post")]HttpRequestMessage req,
            [OrchestrationClient]DurableOrchestrationClient starter,
            TraceWriter log)
        {
            // Function input comes from the request content.
            FunctionParams requestData = await req.Content.ReadAsAsync<FunctionParams>();

            // Starting a new orchestrator with request data
            string instanceId = await starter.StartNewAsync("HttpTrigger_Orchestrator", requestData);

            log.Info($"Started orchestration with ID = '{instanceId}'.");

            var response = starter.CreateCheckStatusResponse(req, instanceId);
            return response;
        }

        [FunctionName("HttpTrigger_Orchestrator")]
        public static async Task<List<string>> RunOrchestrator(
            [OrchestrationTrigger] DurableOrchestrationContext context)
        {
            var outputs = new List<string>();

            outputs.Add(await context.CallActivityAsync<string>("Trigger_Prepare_Version", context.GetInput<FunctionParams>()));

            return outputs;
        }

        [FunctionName("Trigger_Prepare_Version")]
        public static async Task Run([ActivityTrigger]FunctionParams funcParams, TraceWriter log, ExecutionContext context)
        {
            if (!funcParams.ReleaseName.StartsWith("2.", StringComparison.OrdinalIgnoreCase))
            {
                log.Info($"We don't need releases starting with anything other than 2.x. The provided release name is: {funcParams.ReleaseName}");
                return;
            }

            log.Info("Started preparing version");
            var resourcesPath = context.FunctionAppDirectory;
            var repoPath = Path.GetFullPath(Path.Combine(resourcesPath, @"..\Target-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss")));

            log.Info($"Repo path is: {repoPath}");

            try
            {
                var co = new CloneOptions
                {
                    CredentialsProvider = Handler
                };
                var gitPath = Repository.Clone($"https://github.com/{GitRepoOwner}/{GitRepoName}.git", repoPath, co);
                using (var repo = new Repository(gitPath))
                {
                    var repoDir = new DirectoryInfo(repoPath);
                    repoDir.Empty(true);

                    log.Info($"Started downloading ghost version: {funcParams.ReleaseUrl}");
                    await repoDir.DownloadGhostVersion(funcParams.ReleaseUrl);
                    log.Info($"Finished downloading ghost version: {funcParams.ReleaseUrl}");

                    log.Info($"Started enriching package.json");
                    repoDir.EnrichPackageJson();
                    log.Info($"Finished enriching package.json");

                    log.Info($"Started copying additional files into the release directory");
                    var azureResourcesDir = new DirectoryInfo(Path.Combine(resourcesPath, "AzureDeployment"));
                    azureResourcesDir.CopyFilesRecursively(repoDir);
                    log.Info($"Finished copying additional files into the release directory");

                    Commands.Stage(repo, "*");
                    log.Info($"All changes were staged.");

                    var author = new Signature(GitAuthorName, GitAuthorEmail, DateTime.Now);
                    var commit = repo.Commit($"Add v{funcParams.ReleaseName}", author, author);
                    log.Info($"Commit {commit.Id} has been created.");

                    var options = new PushOptions
                    {
                        CredentialsProvider = Handler
                    };
                    log.Info($"Pushing to remote.");
                    repo.Network.Push(repo.Branches[GitRepoBranch], options);
                    log.Info($"Pushed to remote.");

                    log.Info($"Creating release.");
                    await CreateRelease(funcParams.ReleaseName, funcParams.ReleaseNotes);
                    log.Info($"Creating release.");
                }
            }
            catch (Exception e)
            {
                log.Error(e.Message);
                log.Error(e.StackTrace);
            }
        }
    }
}