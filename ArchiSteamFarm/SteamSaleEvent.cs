//     _                _      _  ____   _                           _____
//    / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
//   / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
//  / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
// /_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|
// 
//  Copyright 2015-2017 Łukasz "JustArchi" Domeradzki
//  Contact: JustArchi@JustArchi.net
// 
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
// 
//  http://www.apache.org/licenses/LICENSE-2.0
//      
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.

using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Localization;
using HtmlAgilityPack;

namespace ArchiSteamFarm {
	internal sealed class SteamSaleEvent : IDisposable {
		private const byte MaxSingleQueuesDaily = 3; // This is mainly a pre-caution for infinite queue clearing

		private readonly Bot Bot;
		private readonly Timer SteamDiscoveryQueueTimer;

		internal SteamSaleEvent(Bot bot) {
			Bot = bot ?? throw new ArgumentNullException(nameof(bot));

			SteamDiscoveryQueueTimer = new Timer(
				async e => await ExploreDiscoveryQueue().ConfigureAwait(false),
				null,
				TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(Program.LoadBalancingDelay * Bot.Bots.Count), // Delay
				TimeSpan.FromHours(6.1) // Period
			);
		}

		public void Dispose() => SteamDiscoveryQueueTimer.Dispose();

		private async Task ExploreDiscoveryQueue() {
			if (!Bot.IsConnectedAndLoggedOn) {
				return;
			}

			Bot.ArchiLogger.LogGenericTrace(Strings.Starting);

			for (byte i = 0; (i < MaxSingleQueuesDaily) && (await IsDiscoveryQueueAvailable().ConfigureAwait(false)).GetValueOrDefault(); i++) {
				HashSet<uint> queue = await Bot.ArchiWebHandler.GenerateNewDiscoveryQueue().ConfigureAwait(false);
				if ((queue == null) || (queue.Count == 0)) {
					Bot.ArchiLogger.LogGenericTrace(string.Format(Strings.ErrorIsEmpty, nameof(queue)));
					break;
				}

				Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.ClearingDiscoveryQueue, i));

				// We could in theory do this in parallel, but who knows what would happen...
				foreach (uint queuedAppID in queue) {
					if (await Bot.ArchiWebHandler.ClearFromDiscoveryQueue(queuedAppID).ConfigureAwait(false)) {
						continue;
					}

					Bot.ArchiLogger.LogGenericWarning(Strings.WarningFailed);
					return;
				}

				Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.DoneClearingDiscoveryQueue, i));
			}

			Bot.ArchiLogger.LogGenericTrace(Strings.Done);
		}

		public async Task<bool> ExploreDiscoveryQueueCommand() {
			if (!Bot.IsConnectedAndLoggedOn) {
				return false;
			}

			Bot.ArchiLogger.LogGenericTrace(Strings.Starting);

			for (byte i = 0; (i < MaxSingleQueuesDaily) && (await IsDiscoveryQueueAvailable().ConfigureAwait(false)).GetValueOrDefault(); i++) {
				HashSet<uint> queue = await Bot.ArchiWebHandler.GenerateNewDiscoveryQueue().ConfigureAwait(false);
				if ((queue == null) || (queue.Count == 0)) {
					Bot.ArchiLogger.LogGenericTrace(string.Format(Strings.ErrorIsEmpty, nameof(queue)));
					break;
				}

				Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.ClearingDiscoveryQueue, i));

				// We could in theory do this in parallel, but who knows what would happen...
				foreach (uint queuedAppID in queue) {
					if (await Bot.ArchiWebHandler.ClearFromDiscoveryQueue(queuedAppID).ConfigureAwait(false)) {
						continue;
					}

					Bot.ArchiLogger.LogGenericWarning(Strings.WarningFailed);
					return false;
				}

				Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.DoneClearingDiscoveryQueue, i));
			}

			Bot.ArchiLogger.LogGenericTrace(Strings.Done);

			return true;
		}

		public async Task<bool?> IsDiscoveryQueueAvailable() {
			HtmlDocument htmlDocument = await Bot.ArchiWebHandler.GetDiscoveryQueuePage().ConfigureAwait(false);
			if (htmlDocument == null) {
				return null;
			}

			HtmlNode htmlNode = htmlDocument.DocumentNode.SelectSingleNode("//div[@class='subtext']");
			if (htmlNode == null) {
				// Valid, no cards for exploring the queue available
				return false;
			}

			string text = htmlNode.InnerText;
			if (string.IsNullOrEmpty(text)) {
				Bot.ArchiLogger.LogNullError(nameof(text));
				return null;
			}

			Bot.ArchiLogger.LogGenericTrace(text);

			// It'd make more sense to check against "Come back tomorrow", but it might not cover out-of-the-event queue
			bool result = text.StartsWith("You can get ", StringComparison.Ordinal);
			return result;
		}

		public async Task<bool> VoteForSteamAwards() {
			HtmlDocument htmlDocument = await Bot.ArchiWebHandler.GetSteamAwardsPage().ConfigureAwait(false);
			if (htmlDocument == null) {
				Bot.ArchiLogger.LogNullError("htmlDocument", "VoteForSteamAwards");
				return false;
			}

			HtmlNodeCollection votePanelNodes = htmlDocument.DocumentNode.SelectNodes("//div[@class='vote_nominations store_horizontal_autoslider']");
			if (votePanelNodes == null) {
				Bot.ArchiLogger.LogNullError("noVotePanels", "VoteForSteamAwards");
				return false;
			}

			List<bool> successes = new List<bool>();

			foreach (HtmlNode votePanelNode in votePanelNodes) {
				HtmlNode yourVoteNode = votePanelNode.SelectSingleNode("./div[@class='vote_nomination your_vote']");
				if (yourVoteNode == null) {
					uint voteID;
					if (uint.TryParse(votePanelNode.GetAttributeValue("data-voteid", (string) null), out voteID)) {
						HtmlNodeCollection nominationNodes = votePanelNode.SelectNodes("./div[starts-with(@class, 'vote_nomination')]");
						if (nominationNodes == null) {
							Bot.ArchiLogger.LogNullError("voteNodes", "VoteForSteamAwards");
							successes.Add(false);
						} else {
							int count = nominationNodes.Count;
							int randomIndex = Utilities.RandomNext(count);

							Bot.ArchiLogger.LogGenericInfo("nominationGamesCount", count.ToString());
							Bot.ArchiLogger.LogGenericInfo("nominationGameRandomIndex", randomIndex.ToString());

							uint appIDToVoteFor;

							if (uint.TryParse(nominationNodes[randomIndex].GetAttributeValue("data-vote-appid", (string) null), out appIDToVoteFor)) {
								Bot.ArchiLogger.LogGenericInfo("nominationVoteID", voteID.ToString());
								Bot.ArchiLogger.LogGenericInfo("nominationAppID", appIDToVoteFor.ToString());

								bool success = await Bot.ArchiWebHandler.SteamAwardsVote(voteID, appIDToVoteFor).ConfigureAwait(false);

								if (success) {
									Bot.ArchiLogger.LogGenericInfo("successVote", "voteid: " + voteID + ", appid: " + appIDToVoteFor);
								} else {
									Bot.ArchiLogger.LogGenericError("failedVote", "voteid: " + voteID + ", appid: " + appIDToVoteFor);
								}

								successes.Add(success);

							} else {
								Bot.ArchiLogger.LogNullError("appID", "VoteForSteamAwards");
								successes.Add(false);
							}
						}
					} else {
						Bot.ArchiLogger.LogNullError("voteID", "VoteForSteamAwards");
						successes.Add(false);
					}
				}
			}

			return successes.All(x => x);
		}
	}
}