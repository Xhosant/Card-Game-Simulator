/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/. */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using CardGameDef;
using CardGameDef.Unity;
using Cgs.Menu;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityExtensionMethods;

[assembly: InternalsVisibleTo("PlayMode")]

namespace Cgs
{
    public class CardGameManager : MonoBehaviour
    {
        // Show all Debug.Log() to help with debugging?
        public const bool IsMessengerDebugLogVerbose = false;
        public const string PlayerPrefsDefaultGame = "DefaultGame";
        public const string GameUrl = "GameUrl";
        public const string BranchCallbackErrorMessage = "Branch Callback Error!: ";
        public const string BranchCallbackWarning = "Branch Callback has GameUrl, but it is not a string?";
        public const string DefaultNameWarning = "Found game with default name. Deleting it.";
        public const string SelectionErrorMessage = "Could not select the card game because it is not recognized!: ";
        public const string DownloadErrorMessage = "Error downloading game!: ";
        public const string LoadErrorMessage = "Error loading game!: ";

        public const string LoadErrorPrompt =
            "Error loading game! The game may be corrupted. Delete (note that any decks would also be deleted)?";

        public const string CardsLoadedMessage = "{0} cards loaded!";
        public const string CardsLoadingMessage = "{0} cards loading...";
        public const string SetCardsLoadedMessage = "{0} set cards loaded!";
        public const string SetCardsLoadingMessage = "{0} set cards loading...";
        public const string DeleteErrorMessage = "Error deleting game!: ";
        public const string DeleteWarningMessage = "Please download additional card games before deleting.";

        public const string DeletePrompt =
            "Deleting a card game also deletes all decks saved for that card game. Are you sure you would like to delete this card game?";

        public const string ShareBranchMessage = "Get CGS for {0}: {1}";

        public const string ShareUrlMessage =
            "CGS Deep Link Not Found!\nThe AutoUpdate Url for {0} has been copied to your clipboard:\n{1}";

        public const string ShareWarningMessage =
            "Sharing {0} on CGS requires that it be uploaded to the web.\nIf you would like help with this upload, please contact david@finoldigital.com";

        public const int CardsLoadingMessageThreshold = 60;
        public const int PixelsPerInch = 100;

        public static CardGameManager Instance
        {
            get
            {
                if (IsQuitting)
                    return null;
                if (_instance == null)
                    _instance = GameObject.FindGameObjectWithTag(Tags.CardGameManager).GetComponent<CardGameManager>();
                return _instance;
            }
        }

        private static CardGameManager _instance;

        public static UnityCardGame Current { get; private set; } = UnityCardGame.UnityInvalid;
        public static bool IsQuitting { get; private set; }

        public bool IsSearchingForServer { get; set; }

        public SortedDictionary<string, UnityCardGame> AllCardGames { get; } =
            new SortedDictionary<string, UnityCardGame>();

        public UnityCardGame Previous
        {
            get
            {
                UnityCardGame previous = AllCardGames.Values.LastOrDefault() ?? UnityCardGame.UnityInvalid;

                using SortedDictionary<string, UnityCardGame>.Enumerator allCardGamesEnum =
                    AllCardGames.GetEnumerator();
                var found = false;
                while (!found && allCardGamesEnum.MoveNext())
                {
                    if (allCardGamesEnum.Current.Value != Current)
                        previous = allCardGamesEnum.Current.Value;
                    else
                        found = true;
                }

                return previous;
            }
        }

        public UnityCardGame Next
        {
            get
            {
                UnityCardGame next = AllCardGames.Values.FirstOrDefault() ?? UnityCardGame.UnityInvalid;

                using SortedDictionary<string, UnityCardGame>.Enumerator allCardGamesEnum =
                    AllCardGames.GetEnumerator();
                var found = false;
                while (!found && allCardGamesEnum.MoveNext())
                    if (allCardGamesEnum.Current.Value == Current)
                        found = true;
                if (allCardGamesEnum.MoveNext())
                    next = allCardGamesEnum.Current.Value;

                return next;
            }
        }

        public HashSet<UnityAction> OnSceneActions { get; } = new HashSet<UnityAction>();

        public HashSet<Canvas> CardCanvases { get; } = new HashSet<Canvas>();

        public Canvas CardCanvas
        {
            get
            {
                Canvas topCanvas = null;
                CardCanvases.RemoveWhere((canvas) => canvas == null);
                foreach (Canvas canvas in CardCanvases)
                    if (canvas.gameObject.activeSelf &&
                        (topCanvas == null || canvas.sortingOrder > topCanvas.sortingOrder))
                        topCanvas = canvas;
                return topCanvas;
            }
        }

        public HashSet<Canvas> ModalCanvases { get; } = new HashSet<Canvas>();

        public Canvas ModalCanvas
        {
            get
            {
                Canvas topCanvas = null;
                ModalCanvases.RemoveWhere((canvas) => canvas == null);
                foreach (Canvas canvas in ModalCanvases)
                    if (canvas.gameObject.activeSelf &&
                        (topCanvas == null || canvas.sortingOrder > topCanvas.sortingOrder))
                        topCanvas = canvas;
                return topCanvas;
            }
        }

        public Dialog Messenger
        {
            get
            {
                if (_messenger != null) return _messenger;
                _messenger = Instantiate(Resources.Load<GameObject>("Dialog")).GetOrAddComponent<Dialog>();
                _messenger.transform.SetParent(transform);
                return _messenger;
            }
        }

        private Dialog _messenger;

        private ProgressBar Progress
        {
            get
            {
                if (_progress != null) return _progress;
                _progress = Instantiate(Resources.Load<GameObject>("ProgressBar")).GetOrAddComponent<ProgressBar>();
                _progress.transform.SetParent(transform);
                return _progress;
            }
        }

        private ProgressBar _progress;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            UnityCardGame.UnityInvalid.CoroutineRunner = this;
            DontDestroyOnLoad(gameObject);

            if (!Directory.Exists(UnityCardGame.GamesDirectoryPath))
                CreateDefaultCardGames();
            LookupCardGames();

            if (Debug.isDebugBuild)
                Application.logMessageReceived += ShowLogToUser;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;

            ResetCurrentToDefault();
        }

        private void CreateDefaultCardGames()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            UnityFileMethods.ExtractAndroidStreamingAssets(UnityCardGame.GamesDirectoryPath);
#elif UNITY_WEBGL
            if (!Directory.Exists(UnityCardGame.GamesDirectoryPath))
                Directory.CreateDirectory(UnityCardGame.GamesDirectoryPath);
            string standardPlayingCardsDirectory =
                UnityCardGame.GamesDirectoryPath + "/" + Tags.StandardPlayingCardsDirectoryName;
            if (!Directory.Exists(standardPlayingCardsDirectory))
                Directory.CreateDirectory(standardPlayingCardsDirectory);
            File.WriteAllText(standardPlayingCardsDirectory + "/" + Tags.StandardPlayingCardsJsonFileName,
                Tags.StandPlayingCardsJsonFileContent);
            string dominoesDirectory = UnityCardGame.GamesDirectoryPath + "/" + Tags.DominoesDirectoryName;
            if (!Directory.Exists(dominoesDirectory))
                Directory.CreateDirectory(dominoesDirectory);
            File.WriteAllText(dominoesDirectory + "/" + Tags.DominoesJsonFileName, Tags.DominoesJsonFileContent);
            StartCoroutine(
                UnityFileMethods.SaveUrlToFile(Tags.DominoesCardBackUrl, dominoesDirectory + "/CardBack.png"));
            string mahjongDirectory = UnityCardGame.GamesDirectoryPath + "/" + Tags.MahjongDirectoryName;
            if (!Directory.Exists(mahjongDirectory))
                Directory.CreateDirectory(mahjongDirectory);
            File.WriteAllText(mahjongDirectory + "/" + Tags.MahjongJsonFileName, Tags.MahjongJsonFileContent);
            StartCoroutine(UnityFileMethods.SaveUrlToFile(Tags.MahjongCardBackUrl, mahjongDirectory + "/CardBack.png"));
            string arcmageDirectory = UnityCardGame.GamesDirectoryPath + "/" + Tags.ArcmageDirectoryName;
            if (!Directory.Exists(arcmageDirectory))
                Directory.CreateDirectory(arcmageDirectory);
            File.WriteAllText(arcmageDirectory + "/" + Tags.ArcmageJsonFileName, Tags.ArcmageJsonFileContent);
            StartCoroutine(UnityFileMethods.SaveUrlToFile(Tags.ArcmageCardBackUrl, arcmageDirectory + "/CardBack.png"));
#else
            UnityFileMethods.CopyDirectory(Application.streamingAssetsPath, UnityCardGame.GamesDirectoryPath);
#endif
        }

        internal void LookupCardGames()
        {
            if (!Directory.Exists(UnityCardGame.GamesDirectoryPath) ||
                Directory.GetDirectories(UnityCardGame.GamesDirectoryPath).Length < 1)
                CreateDefaultCardGames();

            foreach (string gameDirectory in Directory.GetDirectories(UnityCardGame.GamesDirectoryPath))
            {
                string gameDirectoryName = gameDirectory.Substring(UnityCardGame.GamesDirectoryPath.Length + 1);
                (string gameName, _) = CardGame.GetNameAndHost(gameDirectoryName);
                if (gameName.Equals(CardGame.DefaultName))
                {
                    Debug.LogWarning(DefaultNameWarning);
                    try
                    {
                        Directory.Delete(gameDirectory, true);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError(DeleteErrorMessage + ex.Message);
                    }
                }
                else
                {
                    var newCardGame = new UnityCardGame(this, gameDirectoryName);
                    newCardGame.ReadProperties();
                    if (!string.IsNullOrEmpty(newCardGame.Error))
                        Debug.LogError(LoadErrorMessage + newCardGame.Error);
                    else
                        AllCardGames[newCardGame.Id] = newCardGame;
                }
            }
        }

        private void ShowLogToUser(string logString, string stackTrace, LogType type)
        {
            // ReSharper disable once RedundantLogicalConditionalExpressionOperand
            // ReSharper disable once ConditionIsAlwaysTrueOrFalse
            if (this != null && Messenger != null && (IsMessengerDebugLogVerbose || !LogType.Log.Equals(type)))
                Messenger.Show(logString);
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ResetGameScene();
        }

        private void OnSceneUnloaded(Scene scene)
        {
            OnSceneActions.Clear();
        }

#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
        public void MobileBranchCallback(Dictionary<string, object> parameters, string error)
        {
            Debug.Log("Branch.Callback");
            if (!string.IsNullOrEmpty(error))
            {
                Debug.LogWarning(BranchCallbackErrorMessage + error);
                return;
            }

            if (!parameters.TryGetValue(GameUrl, out object gameUrlObj))
            {
                Debug.LogWarning(BranchCallbackWarning);
                return;
            }

            string gameUrl = gameUrlObj as string;
            if (!string.IsNullOrEmpty(gameUrl))
                StartCoroutine(GetCardGame(gameUrl));
            else
                Debug.LogWarning(BranchCallbackWarning);
        }
#endif

#if (UNITY_STANDALONE_WIN || UNITY_WSA || ENABLE_WINMD_SUPPORT)
        public void WindowsBranchCallback(BranchSdk.BranchUniversalObject buo, BranchSdk.BranchLinkProperties link,
            BranchSdk.BranchError error)
        {
            Debug.Log("Branch.Callback");
            if (error != null && !string.IsNullOrEmpty(error.GetMessage()))
            {
                Debug.LogWarning(BranchCallbackErrorMessage + error);
                return;
            }

            if (link?.ControlParams == null || !link.ControlParams.TryGetValue(GameUrl, out string gameUrl))
            {
                Debug.LogWarning(BranchCallbackErrorMessage);
                return;
            }

            if (!string.IsNullOrEmpty(gameUrl))
                StartCoroutine(GetCardGame(gameUrl));
            else
                Debug.LogWarning(BranchCallbackWarning);
        }
#endif

        // Note: Does NOT Reset Game Scene
        internal void ResetCurrentToDefault()
        {
            string preferredGameId =
                PlayerPrefs.GetString(PlayerPrefsDefaultGame, Tags.StandardPlayingCardsDirectoryName);
            Current = AllCardGames.TryGetValue(preferredGameId, out UnityCardGame currentGame) &&
                      string.IsNullOrEmpty(currentGame.Error)
                ? currentGame
                : (AllCardGames.FirstOrDefault().Value ?? UnityCardGame.UnityInvalid);
        }

        public IEnumerator GetCardGame(string gameUrl)
        {
            Debug.Log("GetCardGame: Starting...");
            // If user attempts to download a game they already have, we should just update that game
            UnityCardGame existingGame = null;
            foreach (UnityCardGame cardGame in AllCardGames.Values)
                if (cardGame.AutoUpdateUrl.Equals(new Uri(gameUrl)))
                    existingGame = cardGame;
            Debug.Log("GetCardGame: Existing game search complete...");
            if (existingGame != null)
            {
                Debug.Log("GetCardGame: Existing game found; updating...");
                yield return UpdateCardGame(existingGame);
                Debug.Log("GetCardGame: Existing game found; updated!");
                if (string.IsNullOrEmpty(existingGame.Error))
                    Select(existingGame.Id);
                else
                    Debug.LogError("GetCardGame: Not selecting card game because of error after update");
            }
            else
                yield return DownloadCardGame(gameUrl);

            Debug.Log("GetCardGame: Done!");
        }

        private IEnumerator DownloadCardGame(string gameUrl)
        {
            Debug.Log("DownloadCardGame: start");
            var cardGame = new UnityCardGame(this, CardGame.DefaultName, gameUrl);

            Progress.Show(cardGame);
            yield return cardGame.Download();
            Progress.Hide();

            cardGame.Load(UpdateCardGame, LoadCards, LoadSetCards);

            if (!string.IsNullOrEmpty(cardGame.Error))
            {
                Debug.LogError(DownloadErrorMessage + cardGame.Error);
                Messenger.Show(DownloadErrorMessage + cardGame.Error);

                if (!Directory.Exists(cardGame.GameDirectoryPath))
                    yield break;

                try
                {
                    Directory.Delete(cardGame.GameDirectoryPath, true);
                }
                catch (Exception ex)
                {
                    Debug.LogError(DeleteErrorMessage + ex.Message);
                }
            }
            else
            {
                AllCardGames[cardGame.Id] = cardGame;
                Select(cardGame.Id);
            }

            Debug.Log("DownloadCardGame: end");
        }

        public IEnumerator UpdateCardGame(UnityCardGame cardGame)
        {
            cardGame ??= Current;

            Progress.Show(cardGame);
            yield return cardGame.Download();
            Progress.Hide();

            // Notify about the failed update, but otherwise ignore errors
            if (!string.IsNullOrEmpty(cardGame.Error))
            {
                Debug.LogError(DownloadErrorMessage + cardGame.Error);
                Messenger.Show(DownloadErrorMessage + cardGame.Error);
                cardGame.ClearError();
            }

            cardGame.Load(UpdateCardGame, LoadCards, LoadSetCards);
            if (cardGame == Current)
                ResetGameScene();
        }

        private IEnumerator LoadCards(UnityCardGame cardGame)
        {
            cardGame ??= Current;

            for (int page = cardGame.AllCardsUrlPageCountStartIndex;
                page < cardGame.AllCardsUrlPageCountStartIndex + cardGame.AllCardsUrlPageCount;
                page++)
            {
                cardGame.LoadCards(page);
                if (page == cardGame.AllCardsUrlPageCountStartIndex &&
                    cardGame.AllCardsUrlPageCount > CardsLoadingMessageThreshold)
                    Messenger.Show(string.Format(CardsLoadingMessage, cardGame.Name));
                yield return null;
            }

            if (!string.IsNullOrEmpty(cardGame.Error))
                Debug.LogError(LoadErrorMessage + cardGame.Error);
            else if (cardGame.AllCardsUrlPageCount > CardsLoadingMessageThreshold)
                Messenger.Show(string.Format(CardsLoadedMessage, cardGame.Name));
        }

        private IEnumerator LoadSetCards(UnityCardGame cardGame)
        {
            cardGame ??= Current;

            var setCardsLoaded = false;
            foreach (Set set in cardGame.Sets.Values)
            {
                if (string.IsNullOrEmpty(set.CardsUrl))
                    continue;
                if (!setCardsLoaded)
                    Messenger.Show(string.Format(SetCardsLoadingMessage, cardGame.Name));
                setCardsLoaded = true;
                string setCardsFilePath = Path.Combine(cardGame.SetsDirectoryPath,
                    UnityFileMethods.GetSafeFileName(set.Code + UnityFileMethods.JsonExtension));
                if (!File.Exists(setCardsFilePath))
                    yield return UnityFileMethods.SaveUrlToFile(set.CardsUrl, setCardsFilePath);
                if (File.Exists(setCardsFilePath))
                    cardGame.LoadCards(setCardsFilePath, set.Code);
                else
                {
                    Debug.LogError(LoadErrorMessage + set.CardsUrl);
                    yield break;
                }
            }

            if (!string.IsNullOrEmpty(cardGame.Error))
                Debug.LogError(LoadErrorMessage + cardGame.Error);
            else if (setCardsLoaded)
                Messenger.Show(string.Format(SetCardsLoadedMessage, cardGame.Name));
        }

        public void Select(string gameId)
        {
            if (string.IsNullOrEmpty(gameId) || !AllCardGames.ContainsKey(gameId))
            {
                Debug.LogError(SelectionErrorMessage + gameId);
                Messenger.Show(SelectionErrorMessage + gameId);
                return;
            }

            Current = AllCardGames[gameId];
            ResetGameScene();
        }

        internal void ResetGameScene()
        {
            if (!Current.HasLoaded)
            {
                Current.Load(UpdateCardGame, LoadCards, LoadSetCards);
                if (Current.IsDownloading)
                    return;
            }

            if (!string.IsNullOrEmpty(Current.Error))
            {
                Debug.LogError(LoadErrorMessage + Current.Error);
                Messenger.Ask(LoadErrorPrompt, IgnoreCurrentErroredGame, Delete);
                return;
            }

#if UNITY_WEBGL
            foreach (UnityCardGame game in AllCardGames.Values)
                game.ReadProperties();
#endif

            // Now is the safest time to set this game as the preferred default game for the player
            PlayerPrefs.SetString(PlayerPrefsDefaultGame, Current.Id);

            // Each scene is responsible for adding to OnSceneActions, but they may not remove
            OnSceneActions.RemoveWhere((action) => action == null);
            foreach (UnityAction action in OnSceneActions)
                action();
        }

        private void IgnoreCurrentErroredGame()
        {
            Current.ClearError();
            ResetCurrentToDefault();
            ResetGameScene();
        }

        public void PromptDelete()
        {
            if (AllCardGames.Count > 1)
                Messenger.Prompt(DeletePrompt, Delete);
            else
                Messenger.Show(DeleteWarningMessage);
        }

        private void Delete()
        {
            if (AllCardGames.Count < 1)
            {
                Debug.LogError(DeleteErrorMessage + DeleteWarningMessage);
                return;
            }

            try
            {
                Directory.Delete(Current.GameDirectoryPath, true);
                AllCardGames.Remove(Current.Id);
                ResetCurrentToDefault();
                ResetGameScene();
            }
            catch (Exception ex)
            {
                Debug.LogError(DeleteErrorMessage + ex.Message);
            }
        }

        public void Share()
        {
            Debug.Log("CGS Share:: Deep:" + Current.CgsDeepLink + " Auto:" + Current.AutoUpdateUrl);
            if (Current.CgsDeepLink != null && Current.CgsDeepLink.IsWellFormedOriginalString())
            {
                string shareMessage = string.Format(ShareBranchMessage, Current.Name, Current.CgsDeepLink);
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
                var nativeShare = new NativeShare();
                nativeShare.SetText(shareMessage).Share();
#else
                UniClipboard.SetText(shareMessage);
                Messenger.Show(shareMessage);
#endif
            }
            else if (Current.AutoUpdateUrl != null && Current.AutoUpdateUrl.IsWellFormedOriginalString())
            {
                UniClipboard.SetText(Current.AutoUpdateUrl.ToString());
                Messenger.Show(string.Format(ShareUrlMessage, Current.Name, Current.AutoUpdateUrl));
            }
            else
            {
                Debug.LogWarningFormat(ShareWarningMessage, Current.Name);
                Messenger.Show(string.Format(ShareWarningMessage, Current.Name));
            }
        }

        private void LateUpdate()
        {
            Inputs.WasFocusBack = Inputs.IsFocusBack;
            Inputs.WasFocusNext = Inputs.IsFocusNext;
            Inputs.WasDown = Inputs.IsDown;
            Inputs.WasUp = Inputs.IsUp;
            Inputs.WasLeft = Inputs.IsLeft;
            Inputs.WasRight = Inputs.IsRight;
            Inputs.WasPageVertical = Inputs.IsPageVertical;
            Inputs.WasPageDown = Inputs.IsPageDown;
            Inputs.WasPageUp = Inputs.IsPageUp;
            Inputs.WasPageHorizontal = Inputs.IsPageHorizontal;
            Inputs.WasPageLeft = Inputs.IsPageLeft;
            Inputs.WasPageRight = Inputs.IsPageRight;
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnApplicationQuit()
        {
            IsQuitting = true;
        }
    }
}
