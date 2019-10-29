﻿namespace Cosmos.Samples
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos;
    using Microsoft.Extensions.Configuration;
    using Newtonsoft.Json;

    // ----------------------------------------------------------------------------------------------------------
    // Prerequisites - 
    // 
    // 1. An Azure Cosmos account - 
    //    https://azure.microsoft.com/en-us/itemation/articles/itemdb-create-account/
    //
    // 2. Microsoft.Azure.Cosmos NuGet package - 
    //    http://www.nuget.org/packages/Microsoft.Azure.Cosmos/ 
    // ----------------------------------------------------------------------------------------------------------
    // Sample - demonstrates the basic usage of the Batch API that allows atomic CRUD operations against items
    // that have the same partition key in a container.
    // ----------------------------------------------------------------------------------------------------------

    public class Program
    {
        private const string databaseId = "samples";
        private const string containerId = "batchApi";
        private static readonly JsonSerializer Serializer = new JsonSerializer();

        private static Database database = null;

        // <Main>
        public static async Task Main(string[] args)
        {
            try
            {
                // Read the Cosmos endpointUrl and authorisationKeys from configuration
                // These values are available from the Azure Management Portal on the Cosmos Account Blade under "Keys"
                // Keep these values in a safe & secure location. Together they provide Administrative access to your Cosmos account
                IConfigurationRoot configuration = new ConfigurationBuilder()
                    .AddJsonFile("appSettings.json")
                    .Build();

                string endpoint = configuration["EndPointUrl"];
                if (string.IsNullOrEmpty(endpoint))
                {
                    throw new ArgumentNullException("Please specify a valid endpoint in the appSettings.json");
                }

                string authKey = configuration["AuthorizationKey"];
                if (string.IsNullOrEmpty(authKey) || string.Equals(authKey, "Super secret key"))
                {
                    throw new ArgumentException("Please specify a valid AuthorizationKey in the appSettings.json");
                }

                // Read the Cosmos endpointUrl and authorization key from configuration
                // These values are available from the Azure Management Portal on the Cosmos Account Blade under "Keys"
                // NB > Keep these values in a safe & secure location. Together they provide Administrative access to your Cosmos account
                using (CosmosClient client = new CosmosClient(endpoint, authKey))
                {
                    await Program.InitializeAsync(client);
                    await Program.RunDemoAsync();
                    await Program.CleanupAsync();
                }
            }
            catch (CosmosException cre)
            {
                Console.WriteLine(cre.ToString());
            }
            catch (Exception e)
            {
                Exception baseException = e.GetBaseException();
                Console.WriteLine("Error: {0}, Message: {1}", e.Message, baseException.Message);
            }
            finally
            {
                Console.WriteLine("End of demo, press any key to exit.");
                Console.ReadKey();
            }
        }
        // </Main>

        private static async Task InitializeAsync(CosmosClient client)
        {
            Program.database = await client.CreateDatabaseIfNotExistsAsync(Program.databaseId);

            // Delete the existing container to prevent create item conflicts
            using (await Program.database.GetContainer(containerId).DeleteContainerStreamAsync())
            { }

            Console.WriteLine("The demo will create a 1000 RU/s container, press any key to continue.");
            Console.ReadKey();

            // Create a container with a throughput of 1000 RU/s
            await Program.database.DefineContainer(containerId, "/GameId").CreateAsync(1000);
        }

        private static async Task RunDemoAsync()
        {
            Container gamesContainer = Program.database.GetContainer(containerId);

            // This code demonstrates interactions by a multi-player game service that hosts games with the database to save game state.
            // In this fictional game, players move about the 10x10 map and try to find balls. 
            // The objective is to collect 2 balls of the same color, or a golden ball if one appears.
            // After 5 minutes, if the game is not complete, the player with highest number of balls wins.
            int gameId = 420;
            int playerCount = 3;
            int ballCount = 3;
            List<GameBall> balls = new List<GameBall>();
            List<GameParticipant> players = new List<GameParticipant>();

            Console.WriteLine("At the start of the game, the balls are added on the map, and the players are added ...");

            // The below batch request is used to create the game balls and participants in an atomic fashion
            BatchResponse gameStartResponse = await gamesContainer.CreateBatch(new PartitionKey(gameId))
                .CreateItem<GameBall>(GameBall.Create(gameId, Color.Red, 4, 2))
                .CreateItem<GameBall>(GameBall.Create(gameId, Color.Blue, 6, 4))
                .CreateItem<GameBall>(GameBall.Create(gameId, Color.Red, 8, 8))
                .CreateItem<GameParticipant>(GameParticipant.Create(gameId, "alice"))
                .CreateItem<GameParticipant>(GameParticipant.Create(gameId, "bob"))
                .CreateItem<GameParticipant>(GameParticipant.Create(gameId, "carla"))
                .ExecuteAsync();

            GameParticipant alice, bob, carla;
            GameBall blueBall, secondRedBall;

            using (gameStartResponse)
            {
                // Batch requests do not throw exceptions on execution failures as long as the request is valid, so we need to check the response status explicitly.
                // A HTTP 200 (OK) StatusCode on the BatchResponse indicates that all operations succeeded.
                // If one or more operations within the batch have failed, HTTP 207 (Multistatus) status code may be returned (example later).
                // Other status codes such as HTTP 429 (Too Many Requests) and HTTP 5xx on server errors may also be returned.
                // Given a batch request is atomic, in case any operation within a batch fails, no changes from the batch will be committed.
                if (gameStartResponse.StatusCode != HttpStatusCode.OK)
                {
                    // log exception and handle failure
                }

                // Refresh in-memory state from response
                // The BatchResponse has a list of BatchOperationResult, one for each operation within the batch request in the order of operations.
                for (int index = 0; index < ballCount; index++)
                {
                    // The GetOperationResultAtIndex method returns the result of the batch operation with a Resource deserialized to the provided type.
                    BatchOperationResult<GameBall> gameBallResult = gameStartResponse.GetOperationResultAtIndex<GameBall>(index);
                    balls.Add(gameBallResult.Resource);
                }

                blueBall = balls[1];
                secondRedBall = balls[2];

                for (int index = ballCount; index < gameStartResponse.Count; index++)
                {
                    players.Add(gameStartResponse.GetOperationResultAtIndex<GameParticipant>(index).Resource);
                }

                alice = players.Single(p => p.Nickname == "alice");
                bob = players.Single(p => p.Nickname == "bob");
                carla = players.Single(p => p.Nickname == "carla");
            }

            PrintState(players, balls);

            Console.WriteLine("Alice goes to 6, 4 and finds a blue ball ...");
            balls.Remove(blueBall);
            alice.BlueCount++;

            // Upserts maybe used to replace items or create them if they are not already present.
            // An existing item maybe replaced along with concurrency checks the ETag returned in the responses of earlier requests on the item
            // or without these checks if they are not required.
            // Item deletes may also be a part of batch requests.
            BatchResponse aliceFoundBallResponse = await gamesContainer.CreateBatch(new PartitionKey(gameId))
                .UpsertItem<ParticipantLastActive>(ParticipantLastActive.Create(gameId, "alice"))
                .ReplaceItem<GameParticipant>(alice.Nickname, alice, new BatchItemRequestOptions { IfMatchEtag = alice.ETag })
                .DeleteItem(blueBall.Id)
                .ExecuteAsync();

            using (aliceFoundBallResponse)
            {
                if (aliceFoundBallResponse.StatusCode != HttpStatusCode.OK)
                {
                    // log exception and handle failure
                }

                // Refresh in-memory state from response
                // We only update the etag as we have the rest of the state we care about here already as needed.
                alice.ETag = aliceFoundBallResponse[1].ETag;
            }

            PrintState(players, balls);

            Console.WriteLine("Bob goes to 8, 8 and finds a red ball ...");
            balls.Remove(secondRedBall);
            bob.RedCount++;

            // Stream variants for all batch operations that accept an item are also available for use when the item is available as a Stream.
            Stream bobIsActiveStream = ParticipantLastActive.CreateStream(gameId, "bob");

            BatchResponse bobFoundBallResponse = await gamesContainer.CreateBatch(new PartitionKey(gameId))
                .UpsertItemStream(bobIsActiveStream)
                .ReplaceItemStream(bob.Nickname, Program.AsStream(bob), new BatchItemRequestOptions { IfMatchEtag = bob.ETag })
                .DeleteItem(secondRedBall.Id)
                .ExecuteAsync();

            using (bobFoundBallResponse)
            {
                if (bobFoundBallResponse.StatusCode != HttpStatusCode.OK)
                {
                    // log exception and handle failure
                }

                // Refresh in-memory state from response
                // The resultant item for each operation is also available as a Stream that can be used for example if the response is just
                // going to be transferred to some other system.
                Stream updatedPlayerAsStream = bobFoundBallResponse[1].ResourceStream;

                bob = Program.FromStream<GameParticipant>(updatedPlayerAsStream);
            }

            PrintState(players, balls);

            Console.WriteLine("A golden ball appears near each of the players to select an instant winner ...");
            BatchResponse goldenBallResponse = await gamesContainer.CreateBatch(new PartitionKey(gameId))
               .CreateItem<GameBall>(GameBall.Create(gameId, Color.Gold, 2, 2))
               .CreateItem<GameBall>(GameBall.Create(gameId, Color.Gold, 6, 3))
               // oops - there is already a ball at 8, 7
               .CreateItem<GameBall>(GameBall.Create(gameId, Color.Gold, 8, 7))
               .ExecuteAsync();

            using (goldenBallResponse)
            {
                // If one or more operations within the batch have failed, a HTTP Status Code of 207 MultiStatus could be returned 
                // as the StatusCode of the BatchResponse that indicates that the response needs to be examined to understand details of the 
                // execution and failure. The first operation to fail (for example if we have a conflict because we are trying to create an item 
                // that already exists) will have the StatusCode on its corresponding BatchOperationResult set with the actual failure reason
                // (HttpStatusCode.Conflict in this example).
                // All other operations will be aborted - these would return a status code of HTTP 424 Failed Dependency.
                //
                // Other status codes such as HTTP 429 (Too Many Requests) and HTTP 5xx on server errors may also be returned for the BatchResponse.
                // Given a batch request is atomic, in case any operation within a batch fails, no changes from the batch will be committed.
                if (goldenBallResponse.StatusCode != HttpStatusCode.OK)
                {
                    for (int index = 0; index < goldenBallResponse.Count; index++)
                    {
                        BatchOperationResult operationResult = goldenBallResponse[index];
                        if ((int)operationResult.StatusCode == 424)
                        {
                            // This operation failed because it was batched along with another operation where the latter was the actual cause of failure.
                            continue;
                        }

                        if (operationResult.StatusCode == HttpStatusCode.Conflict)
                        {
                            Console.WriteLine("Creation of the {0}rd golden ball failed because there was already an existing ball at that position.", index + 1);
                        }

                        // Log and handle other failures
                    }
                }
            }

            PrintState(players, balls);

            Console.WriteLine("We need to end the game now; determining the winner as the player with highest balls ...");

            // Batch requests may also be used to atomically read the state of multiple items within a partition key.
            BatchResponse playersResponse = await gamesContainer.CreateBatch(new PartitionKey(gameId))
                .ReadItem(alice.Nickname)
                .ReadItem(bob.Nickname)
                .ReadItem(carla.Nickname)
                .ExecuteAsync();

            GameParticipant winner = null;
            bool isTied = false;

            using (playersResponse)
            {
                if (playersResponse.StatusCode != HttpStatusCode.OK)
                {
                    // log exception and handle failure
                }

                for (int index = 0; index < playerCount; index++)
                {
                    GameParticipant current;

                    if (index == 0)
                    {
                        // The item returned can be made available as the required POCO type using GetOperationResultAtIndex.
                        // A single batch request can be used to read items that can be deserialized to different POCOs as well.
                        current = playersResponse.GetOperationResultAtIndex<GameParticipant>(index).Resource;
                    }
                    else
                    {
                        // The item returned can also instead be accessed directly as a Stream (for example to pass as-is to another component).
                        Stream aliceInfo = playersResponse[index].ResourceStream;

                        current = Program.FromStream<GameParticipant>(aliceInfo);
                    }

                    if (winner == null || current.TotalCount > winner.TotalCount)
                    {
                        winner = current;
                        isTied = false;
                    }
                    else if(current.TotalCount == winner.TotalCount)
                    {
                        isTied = true;
                    }
                }
            }

            if (!isTied)
            {
                Console.WriteLine($"{winner.Nickname} has won the game!\n");
            }
            else
            {
                Console.WriteLine("The game is a tie; there is no clear winner.\n");
            }
        }

        private static void PrintState(List<GameParticipant> players, List<GameBall> balls)
        {
            Console.WriteLine("{0,-8}{1,6}{2,6}{3,6}", "Nick", "Red", "Blue", "Total");
            foreach(GameParticipant player in players)
            {
                Console.WriteLine("{0,-8}{1,6}{2,6}{3,6}", player.Nickname, player.RedCount, player.BlueCount, player.TotalCount);
            }

            Console.Write("Ball positions: ");
            foreach(GameBall ball in balls)
            {
                Console.Write("[{0},{1}] ", ball.XCoord, ball.YCoord);
            }

            Console.WriteLine("\n===================================================================================\n");
        }

        private static async Task CleanupAsync()
        {
            if (Program.database != null)
            {
                await Program.database.DeleteAsync();
            }
        }

        private static Stream AsStream<T>(T obj)
        {
            return new MemoryStream(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(obj)));
        }

        private static T FromStream<T>(Stream stream)
        {
            StreamReader streamReader = new StreamReader(stream);
            return JsonConvert.DeserializeObject<T>(streamReader.ReadToEnd());
        }

        private enum Color
        {
            Red,
            Blue,
            Gold
        }

        private class GameBall
        {
            public int GameId { get; set; }

            public Color Color { get; set; }

            public int XCoord { get; set; }

            public int YCoord { get; set; }

            [JsonProperty("id")]
            internal string Id { get { return $"{XCoord}:{YCoord}"; } }

            [JsonProperty("_etag")]
            internal string ETag { get; set; }

            public static GameBall Create(int gameId, Color color, int xCoord, int yCoord)
            {
                return new GameBall()
                {
                    GameId = gameId,
                    Color = color,
                    XCoord = xCoord,
                    YCoord = yCoord
                };
            }

            public override bool Equals(object obj)
            {
                var ball = obj as GameBall;
                return ball != null &&
                       GameId == ball.GameId &&
                       Color == ball.Color &&
                       XCoord == ball.XCoord &&
                       YCoord == ball.YCoord &&
                       Id == ball.Id;
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(GameId, Color, XCoord, YCoord, Id);
            }
        }

        public class ParticipantLastActive
        {
            public int GameId { get; set; }

            public string Nickname { get; set; }

            [JsonProperty("id")]
            internal string Id { get { return $"Activity_{Nickname}"; } }

            public DateTime LastActive { get; set; }

            public static ParticipantLastActive Create(int gameId, string nickname)
            {
                return new ParticipantLastActive
                {
                    GameId = gameId,
                    Nickname = nickname,
                    LastActive = DateTime.UtcNow
                };
            }

            public static Stream CreateStream(int gameId, string nickname)
            {
                return new MemoryStream(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(ParticipantLastActive.Create(gameId, nickname))));
            }
        }

        public class GameParticipant
        {
            public int GameId { get; set; }

            [JsonProperty("id")]
            public string Nickname { get; set; }

            public int RedCount { get; set; }

            public int BlueCount { get; set; }

            public int TotalCount { get { return this.RedCount + this.BlueCount; } }

            [JsonProperty("_etag")]
            internal string ETag { get; set; }

            public static GameParticipant Create(int gameId, string nickname)
            {
                return new GameParticipant
                {
                    GameId = gameId,
                    Nickname = nickname,
                };
            }

            public override bool Equals(object obj)
            {
                var participant = obj as GameParticipant;
                return participant != null &&
                       GameId == participant.GameId &&
                       Nickname == participant.Nickname &&
                       RedCount == participant.RedCount &&
                       BlueCount == participant.BlueCount;
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(GameId, Nickname, RedCount, BlueCount);
            }
        }
    }
}
