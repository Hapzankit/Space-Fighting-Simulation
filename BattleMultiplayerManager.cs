using Mirror;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Collections;
using LootLocker.Requests;
using System.IO;
using UnityEngine.Networking;

[Serializable]
public struct GunsMultiplayer
{
    [HideInInspector] public string gunName;
    public float accuracy;
    public float firingRate;
    public float damage;
}

public class SyncListGun : SyncList<GunsMultiplayer> { }

public class BattleMultiplayerManager : NetworkBehaviour
{
    #region Attributes


    [Header("SO")]
    public Data data;

    [Header("Int")]
    [SyncVar(hook = nameof(SetParent))] 
    [SerializeField] private int spawnPoint;

    [SyncVar(hook = nameof(OnIsUnderAttackChanged))]
    public int attackingPlayersCount = 0;

    [Header("Lists")]
    public SyncListGun onlinePlayerGuns = new();
    public SyncList<BattleMultiplayerManager> attackingPlayer = new();

    [HideInInspector]
    public List<BattleMultiplayerManager> allPlayers = new();

    [Header("Float")]
    [SyncVar] [SerializeField] private float damageGiven;
    [SyncVar] public float hull;
    [SyncVar] public float maxHull;
    [SyncVar] public float shields;
    [SyncVar] public float evasion;
    [SyncVar] public float jumpSpeed;

    [SyncVar(hook = nameof(OnUpdateUI))]
    [SerializeField] public float damageRatio;

    [SyncVar] public float consecutiveAttackDelay; // Time taken between each consecutive shots.
    private float damageTaken;
    private float damage;
    private float time = 1f;
    [SyncVar] public float shieldSliderValue;
    [SyncVar] public float shieldSliderText;

    [Header("String")]
    [SyncVar] public string playerName;
    [SyncVar] [SerializeField] private string shipSpriteReference;

    [Header("Bool")]
    [SyncVar] public bool battleMode; // True means I have engaged, false means I have disengaged. 
    private bool isDead = false;

    [Header("Netowrk Identity")]
    [SyncVar] public NetworkIdentity engagedWith; // The one we are dealing damage.
    private NetworkIdentity targetShipIdentity;


    #endregion


    #region Server

    /// <summary>
    /// This will set the spawn point that we have used, run it on the server and sync it across.
    /// </summary>
    [Command]
    public void CmdGetSpawnPoint(int newSpawnPoint) => spawnPoint = newSpawnPoint;


    /// <summary>
    /// This will ask the server to give us the spawn point.
    /// </summary>
    [Command]
    private void CmdRequestSpawnPoint()
    {
        int chosenSpawnPoint = CockPitMultiplayerManager.Instance.GenerateRandomNumber();

        RpcReceiveSpawnPoint(chosenSpawnPoint);
    }


    /// <summary>
    /// This will set the damage that we have calculate, run it on the server and sync it across.
    /// </summary>
    [Command]
    public void CmdSetDamage(float damage) => damageGiven = damage;


    /// <summary>
    /// This will set the player name from all data SO, run it on the server and sync it across.
    /// </summary>
    [Command]
    public void CmdSetPlayerName(string newName) => playerName = newName;


    /// <summary>
    /// This will set the my ship default image from all data SO, run it on the server and sync it across.
    /// </summary>
    [Command]
    public void CmdSetShipSprite(string spriteReference)
    {
        shipSpriteReference = spriteReference;

        RpcUpdateShipSprite(shipSpriteReference);
    }


    /// <summary>
    /// This just gives us some info about the other ship that we have clicked.
    /// </summary>
    [Command]
    private void CmdShipClicked(NetworkIdentity shipIdentity)
    {
        if (shipIdentity.TryGetComponent<BattleMultiplayerManager>(out var clickedShipManager))
        {
            string clickedShipName = clickedShipManager.shipSpriteReference;
            string clickedShipPlayerName = clickedShipManager.playerName;

            TargetPrintClickedShipName(connectionToClient, clickedShipName);
            TargetPrintClickedPlayerName(connectionToClient, clickedShipPlayerName);
        }
    }


    /// <summary>
    /// This sync the player stats across.
    /// </summary>
    [Command]
    private void CmdShowPlayerStats(float newHull, float newMaxHull, float newShields, float newEvasion, float newJumpSpeed)
    {
        hull = newHull;
        maxHull = newMaxHull;
        shields = newShields;
        evasion = newEvasion;
        jumpSpeed = newJumpSpeed;
    }


    /// <summary>
    /// This will add guns owned by the player into this list.
    /// </summary>
    [Command]
    private void CmdShowGunsData(GunsMultiplayer gunData) => onlinePlayerGuns.Add(gunData);


    /// <summary>
    /// This will be called when we click on the engage button. This will let us set with whom we have started battling.
    /// </summary>
    [Command]
    private void CmdEngageShip(NetworkIdentity newTargetShipIdentity)
    {
        var targetShipManager = newTargetShipIdentity.GetComponent<BattleMultiplayerManager>();

        targetShipManager.attackingPlayer.Add(this);
        targetShipManager.attackingPlayersCount++;

        engagedWith = newTargetShipIdentity;
    }


    /// <summary>
    /// This will be called when we click on the disengage button. This will let us set with whom we have started battling.
    /// </summary>
    [Command]
    private void CmdDisEngageShip(NetworkIdentity newTargetShipIdentity)
    {
        var targetShipManager = newTargetShipIdentity.GetComponent<BattleMultiplayerManager>();

        targetShipManager.attackingPlayer.Remove(this);
        targetShipManager.attackingPlayersCount--;

        StartCoroutine(nameof(NullEngageShip));
    }


    /// <summary>
    /// This is just used to null the engage ship because its a sync var and can be only done in command
    /// </summary>
    [Command]
    private void CmdNullEngageShip() => StartCoroutine(nameof(NullEngageShip));


    /// <summary>
    /// This function will be request by the client to set the hull on the server.
    /// </summary>
    [Command]
    private void CmdSetHull(float newHull) => hull = newHull;


    /// <summary>
    /// This function will be request by the client to set the damageRatio on the server.
    /// </summary>
    [Command]
    private void CmdSetDamageRatio(float newDamageRatio) => damageRatio = newDamageRatio;


    /// <summary>
    /// We have used this bool is check if we are engaging with someone or not.
    /// </summary>
    [Command]
    private void CmdChangeBattleMode(bool newMode) => battleMode = newMode;


    /// <summary>
    /// This will run on the server which will tell all the clients that this particular player has died.
    /// </summary>
    [Command]
    private void CmdShowDeathAnimationToAll(NetworkIdentity engagedWith) => RpcPlayDeathAnimation(engagedWith);


    /// <summary>
    /// This calculates how much fast/slow the ships will fire.
    /// </summary>
    [Command]
    private void CmdSetBattleDuration(float newBattleDuration) => consecutiveAttackDelay = newBattleDuration;


    /// <summary>
    /// When dead this is be called to clear all the attacking players list for us.
    /// </summary>
    [Command]
    private void CmdClearAttackingPlayerList() => attackingPlayer.Clear();


    /// <summary>
    /// When we killed someone and if they are attacking us, remove them from the list.
    /// </summary>
    [Command]
    private void CmdRemoveParticularAttackingPlayer(NetworkIdentity engagedWith) => attackingPlayer.Remove(engagedWith.GetComponent<BattleMultiplayerManager>());


    /// <summary>
    /// This will help us sync shield data so that we can take less damage if we have a higher shield bar.
    /// </summary>
    [Command]
    public void CmdSyncShieldDeatils(float newShieldSliderValue, float newShieldSliderText)
    {
        shieldSliderValue = newShieldSliderValue;
        shieldSliderText = newShieldSliderText;
    }


    #endregion


    #region Client


    /// <summary>
    /// To calculate the damage taken.
    /// </summary>
    public float CalculateDamageTaken(float accuracy, float evasion, float gunDamage, float shieldValue, 
        float shieldSlider, float firingRate)
    {
        float damageTaken = 0f;
        float rand = UnityEngine.Random.Range(1, 101);

        if (rand <= accuracy)
        {
            if (rand <= evasion)
            {
                damageTaken = (gunDamage - ( gunDamage * (shieldValue * shieldSlider / 100))) * firingRate / 15;
            }
        }
        return damageTaken;
    }


    /// <summary>
    /// This is the function which gets called when the game starts so that we can set the player as the child of the canvas.
    /// </summary>
    private void SetParent(int oldSpawnPoint, int newSpawnPoint) => StartCoroutine(IESetParent(oldSpawnPoint, newSpawnPoint));


    /// <summary>
    /// This is a coroutine which runs initially that set ourselves as the child of the canvas so that we are visible in the screen.
    /// </summary>
    private IEnumerator IESetParent(int oldSpawnPoint, int newSpawnPoint)
    {
        yield return new WaitUntil(() => CockPitMultiplayerManager.Instance != null);

        var managerInstance = CockPitMultiplayerManager.Instance;
        var objTransform = gameObject.transform;

        if (managerInstance.enemyShipsParent == null)
        {
            Debug.LogError("enemyShipsParent is null.");
            yield break;
        }

        objTransform.SetParent(managerInstance.enemyShipsParent.transform, false);

        Debug.Log("Should spawn inside...");

        if (newSpawnPoint >= 0 && newSpawnPoint < managerInstance.enemyShipSpawnPoints.Count)
        {
            var spawnPointTransform = managerInstance.enemyShipSpawnPoints[newSpawnPoint].transform;
            if (spawnPointTransform != null)
            {
                objTransform.position = spawnPointTransform.position;
            }
            else
            {
                Debug.LogError($"Spawn point at index {newSpawnPoint} has a null transform.");
            }
        }
        else
        {
            Debug.LogError("Invalid spawnPoint index: " + newSpawnPoint);
        }
    }


    /// <summary>
    /// Thsi works when the client is joined and ready. We can do any initialization here.
    /// </summary>
    public override void OnStartClient()
    {
        LoadData();
        UpdateShipImage();
        UpdateShipSprite(shipSpriteReference);

        if (!isLocalPlayer) return;

        Debug.Log("We are local player now set us the parent of canvas and set my name and other info...");

        InitializeLocalPlayerData();
    }


    /// <summary>
    /// This loads the Scriptable Objects.
    /// </summary>
    private void LoadData()
    {
        string dataPath = CockPitMultiplayerManager.Instance.useAllDataTest ? "All Data Test" : "All Data";
        data = Resources.Load<Data>(dataPath);

        if (data == null)
        {
            Debug.LogError("Failed to load data from Resources.");
        }
    }


    /// <summary>
    /// This loads the image of the ship from the data.
    /// </summary>
    private void UpdateShipImage()
    {
        if (data == null) return;

        if (gameObject.TryGetComponent<Image>(out var image))
        {
            image.sprite = data.shipData?.shipAvatar;
            image.preserveAspect = true;
        }
    }


    /// <summary>
    /// This sets all the initial data.
    /// </summary>
    private void InitializeLocalPlayerData()
    {
        CmdRequestSpawnPoint();

        if (data != null)
        {
            CmdSetPlayerName(data.playerName);
            CmdSetShipSprite(data.shipData?.shipAvatar?.name);
            CmdShowPlayerStats(data.shipHealth * data.shipMaxHealth, data.shipMaxHealth, data.shipData.shields,
                data.shipData.evasion, data.shipData.jumpSpeed);
            CalculateGunData(data);
        }
    }


    /// <summary>
    /// This is notify the local player of the spawn point given by the server.
    /// </summary>
    [ClientRpc]
    private void RpcReceiveSpawnPoint(int newSpawnPoint)
    {
        if (isLocalPlayer)
        {
            CmdGetSpawnPoint(newSpawnPoint);
        }
    }


    /// <summary>
    /// This will run  once our client is connected and only locally i.e for players we have the authority for.
    /// </summary>
    public override void OnStartLocalPlayer()
    {
        gameObject.GetComponent<Image>().enabled = false;

        CockPitMultiplayerManager.Instance.playerShipImage.color = new(1, 1, 1, 1);
        CockPitMultiplayerManager.Instance.playerShipImage.preserveAspect = true;
        CockPitMultiplayerManager.Instance.playerShipImage.sprite = data.shipData.shipAvatar;
        CockPitMultiplayerManager.Instance.playerName.text = data.playerName;

        CockPitMultiplayerManager.Instance.SetLocalPlayer(this);

        StartCoroutine(MonitorPlayerBattles());
    }


    /// <summary>
    /// This refreshes the parent who has all the clients as its child. This is used at many places where we need to do things locally.
    /// </summary>
    public void GetAllPlayers()
    {
        allPlayers.Clear(); // Clear the existing list first

        foreach (Transform child in CockPitMultiplayerManager.Instance.enemyShipsParent.transform)
        {
            BattleMultiplayerManager player = child.GetComponent<BattleMultiplayerManager>();

            // Check if the component exists and is enabled
            if (player != null && player.enabled)
            {
                allPlayers.Add(player);

                // Append the netId to the game object's name
                player.gameObject.name = $"{player.playerName}_NetID{player.GetComponent<NetworkIdentity>().netId}";
            }
        }
    }



    /// <summary>
    /// This will be called after clicking on the engage button. This changes our battle moce and starting giving damage to other players.
    /// </summary>
    public void StartBattle()
    {
        CmdChangeBattleMode(true);

        StartCoroutine(nameof(EngageBattle));
    }


    /// <summary>
    /// This function will actually let us give damage to ourselfs when battling.
    /// </summary>
    private IEnumerator EngageBattle()
    {
        yield return new WaitUntil(() => engagedWith != null);

        float playerTotalFiringRate = CalculateTotalFiringRate(data);

        while (engagedWith != null && hull >= 0)
        {          
            if (!engagedWith.TryGetComponent<BattleMultiplayerManager>(out var engagedBattleManager))
            {
                CockPitMultiplayerManager.Instance.blueLaser.SetActive(false);
                CockPitMultiplayerManager.Instance.engageButton.SetActive(true);
                CockPitMultiplayerManager.Instance.disEngageButton.SetActive(false);

                CmdChangeBattleMode(false);

                break;  // Exit the loop if the engaged object is destroyed or not under attack anymore
            }

            if(engagedWith.GetComponent<BattleMultiplayerManager>().hull <= 0)
            {
                CmdShowDeathAnimationToAll(engagedWith);

                CockPitMultiplayerManager.Instance.blueLaser.SetActive(false);
                CockPitMultiplayerManager.Instance.engageButton.SetActive(true);
                CockPitMultiplayerManager.Instance.engageButton.GetComponent<Button>().interactable = false;
                CockPitMultiplayerManager.Instance.disEngageButton.SetActive(false);

                engagedWith.transform.GetChild(0).gameObject.SetActive(false);
                engagedWith.transform.GetChild(1).gameObject.SetActive(false);
                engagedWith.transform.GetChild(3).gameObject.SetActive(false);
                engagedWith.transform.GetChild(4).gameObject.SetActive(false);

                CockPitMultiplayerManager.Instance.engagedWithShipImage.sprite = null;
                CockPitMultiplayerManager.Instance.engagedWithShipImage.color = new(1, 1, 1, 0);

                CockPitMultiplayerManager.Instance.engagedWithShipName.text = null;

                CockPitMultiplayerManager.Instance.engagedWithShipHealthBar.transform.parent.gameObject.SetActive(false);

                if (engagedWith.transform.childCount > 5)
                {
                    Transform child = engagedWith.transform.GetChild(5);

                    if (child != null)
                    {
                        Destroy(child.gameObject);
                    }
                }

                CmdChangeBattleMode(false);
                CmdNullEngageShip();
                CmdRemoveParticularAttackingPlayer(engagedWith);

                break;
            }

            damage = 0;

            for (int i = 0; i < data.weaponDatas.Count; i++)
            {
                damage += CalculateDamageTaken(data.weaponDatas[i].data[0].value, engagedWith.GetComponent<BattleMultiplayerManager>().evasion,
                    data.weaponDatas[i].data[2].value, engagedWith.GetComponent<BattleMultiplayerManager>().shieldSliderText,
                    engagedWith.GetComponent<BattleMultiplayerManager>().shieldSliderValue, data.weaponDatas[i].data[1].value);
            }

            CmdSetDamage(damage);

            if(engagedWith.GetComponent<BattleMultiplayerManager>().damageRatio > 0)
            {
                CockPitMultiplayerManager.Instance.engagedWithShipHealthBar.size = engagedWith.GetComponent<BattleMultiplayerManager>().damageRatio;
                CockPitMultiplayerManager.Instance.localPlayer.engagedWith.transform.GetChild(3).GetChild(0).GetComponent<Scrollbar>().size =
                    engagedWith.GetComponent<BattleMultiplayerManager>().damageRatio;
            }

            float currentBattleDuration = 100 / playerTotalFiringRate;
            CmdSetBattleDuration(currentBattleDuration);    

            yield return new WaitForSeconds(currentBattleDuration);
        }
    }


    /// <summary>
    /// This will tell all the clients that this particular player has died in the network.
    /// </summary>
    [ClientRpc]
    public void RpcPlayDeathAnimation(NetworkIdentity engagedWith)
    {
        foreach(var player in CockPitMultiplayerManager.Instance.localPlayer.allPlayers)
        {
            if (player.name.Equals(engagedWith.name))
            {
                player.GetComponent<Image>().enabled = true;
                player.GetComponent<Image>().raycastTarget = false;

                player.transform.GetChild(0).gameObject.SetActive(false);
                player.transform.GetChild(1).gameObject.SetActive(false);
                player.transform.GetChild(3).gameObject.SetActive(false);
                player.transform.GetChild(4).gameObject.SetActive(false);

                player.GetComponent<Animator>().enabled = true;

                StartCoroutine(IEDisableShipImageAfterDeath(player));
            }

            if(player.engagedWith == null)
            {
                if (player.transform.childCount > 5)
                {
                    Transform child = player.transform.GetChild(5);

                    if (child != null)
                    {
                        Destroy(child.gameObject);
                    }
                }
            }
        }
    }


    /// <summary>
    /// This will disable the the ship image whi died so that we cannot click on them again.
    /// </summary>
    private IEnumerator IEDisableShipImageAfterDeath(BattleMultiplayerManager newPlayer)
    {
        yield return new WaitForSeconds(2f);

        newPlayer.GetComponent<Image>().enabled = false;
        newPlayer.GetComponent<BattleMultiplayerManager>().enabled = false;

        CockPitMultiplayerManager.Instance.CheckForOtherPlayersBattle();
    }


    /// <summary>
    /// This set the value of the jump bar when the game starts.
    /// </summary>
    public void JumpBarStart()
    {
        damageRatio = data.shipHealth;

        CmdSetDamageRatio(data.shipHealth);

        CockPitMultiplayerManager.Instance.playerHP.fillAmount = data.shipHealth;
        CockPitMultiplayerManager.Instance.playerHealth.text = MathF.Floor(data.shipHealth * maxHull).ToString();
        CockPitMultiplayerManager.Instance.OnScrollbarValueChanged();
    }


    /// <summary>
    /// To call this update on all the clients.
    /// </summary>
    [ClientRpc]
    private void RpcUpdateShipSprite(string spriteReference) => UpdateShipSprite(spriteReference);


    /// <summary>
    /// This function is used to update sprites locally according the the sprites which is there in the server for other players.
    /// </summary>
    private void UpdateShipSprite(string spriteReference)
    {
        if (!string.IsNullOrEmpty(spriteReference))
        {
            Sprite newSprite = data.leftShipImages.Find(sprite => sprite.name == spriteReference);

            if (newSprite != null)
            {
                if (gameObject.TryGetComponent<Image>(out var image))
                {
                    image.sprite = newSprite;
                    image.preserveAspect = true;
                }
            }
        }
    }


    /// <summary>
    /// Only run on the client system. Store the clicked ship's NetworkIdentity
    /// </summary>
    [ClientCallback]
    private void Update()
    {
        if (!isLocalPlayer) return;

        if (Input.GetMouseButtonDown(0) && !IsEngageButtonClicked())
        {
            if (IsShipUnderMouse(out BattleMultiplayerManager clickedShipManager))
            {
                CockPitMultiplayerManager.Instance.engageButton.GetComponent<Button>().interactable = true;

                DeSelectAllShips();

                CmdShipClicked(clickedShipManager.GetComponent<NetworkIdentity>());

                targetShipIdentity = clickedShipManager.GetComponent<NetworkIdentity>();
                targetShipIdentity.GetComponent<BattleMultiplayerManager>().EnableUI();

                if (engagedWith != targetShipIdentity)
                {
                    CockPitMultiplayerManager.Instance.engageButton.SetActive(true);
                    CockPitMultiplayerManager.Instance.disEngageButton.SetActive(false);
                }
                else
                {
                    CockPitMultiplayerManager.Instance.engageButton.SetActive(false);
                    CockPitMultiplayerManager.Instance.disEngageButton.SetActive(true);
                }

                foreach (var item in data.leftShipImages)
                {
                    if (clickedShipManager.shipSpriteReference.Equals(item.name))
                        CockPitMultiplayerManager.Instance.engagedWithShipImage.sprite = item;
                }

                CockPitMultiplayerManager.Instance.engagedWithShipImage.preserveAspect = true;
            }

            else if(battleMode == false) 
            {
                if (targetShipIdentity != null)
                {
                    targetShipIdentity.GetComponent<BattleMultiplayerManager>().DisableUI();
                    targetShipIdentity = null;
                }
            }
        }

        if (attackingPlayersCount > 0)
        {
            time -= Time.deltaTime;

            if (time <= 0)
            {
                damageTaken = 0;

                foreach (var item in attackingPlayer)
                    damageTaken += item.damageGiven;

                if(damageTaken <= 0)
                {
                    AudioManager.Instance.PlayMusic("Evaded");
                }

                else if(damageTaken > 0)
                {
                    AudioManager.Instance.PlayMusic("Getting Damage");

                    CockPitMultiplayerManager.Instance.DisplayDamage();
                }

                CmdSetHull(hull - damageTaken);
                CmdSetDamageRatio(hull / maxHull);

                foreach (var item in attackingPlayer)
                    time += item.consecutiveAttackDelay;

                time /= attackingPlayer.Count;
            }

            if (hull <= 0.0f && !isDead)
            {
                CockPitMultiplayerManager.Instance.blueLaser.SetActive(false);

                foreach (var p in attackingPlayer)
                {
                    string shipName = p.GetComponent<Image>().sprite.name;
                    data.frontShipImages.ForEach(x =>
                    {
                        if (shipName.Equals(x.name[6..]))
                        {
                            p.GetComponent<Image>().sprite = x;
                        }
                    });

                    if(p.transform.childCount > 5)
                    {
                        Transform child = p.transform.GetChild(5);
                        if (child != null)
                        {
                            Destroy(child.gameObject);
                        }
                    }
                }

                attackingPlayersCount = 0;

                if (engagedWith != null)
                {
                    engagedWith.transform.GetChild(0).gameObject.SetActive(false);
                    engagedWith.transform.GetChild(1).gameObject.SetActive(false);
                    engagedWith.transform.GetChild(3).gameObject.SetActive(false);
                    engagedWith.transform.GetChild(4).gameObject.SetActive(false);

                    CockPitMultiplayerManager.Instance.engagedWithShipImage.sprite = null;
                    CockPitMultiplayerManager.Instance.engagedWithShipImage.color = new(1, 1, 1, 0);

                    CockPitMultiplayerManager.Instance.engagedWithShipName.text = null;

                    CockPitMultiplayerManager.Instance.engagedWithShipHealthBar.transform.parent.gameObject.SetActive(false);
                }

                CockPitMultiplayerManager.Instance.playerDiedPanel.SetActive(true);
                CockPitMultiplayerManager.Instance.playerHP.fillAmount = 0.0f;
                CockPitMultiplayerManager.Instance.playerHealth.text = "0";

                // processesToFinish is used for waiting to the store to lootlocker procesess to finish,
                CockPitMultiplayerManager.Instance.processesToFinish = 0;
                // Set actualNode to Planet 1 when player dies so it will reappear there.
                data.lastVisitedPlanet = "A1";
                data.actualNode = "A1";
                data.shipData = null;
                CockPitMultiplayerManager.Instance.visitedPlanetsManager.UpdateVisitedPlanets("A1");
                SetLootlockerShipHP(data.slot, 999);
                RemoveFromOwnedShipsFile(data.slot);

                CmdChangeBattleMode(false);
                CmdNullEngageShip();
                CmdClearAttackingPlayerList();

                isDead = true;
            }
        }
    }   


    public void RemoveFromOwnedShipsFile(int slot)
    {
        int fileId = 0;
        string fileUrl = "";

        // Get owned ships file
        LootLockerSDKManager.GetAllPlayerFiles((response) =>
        {
            if (response.success)
            {
                for (int i = 0; i < response.items.Length; i++)
                {
                    if (response.items[i].purpose == "OWNED_SHIPS")
                    {
                        fileId = response.items[i].id;
                        fileUrl = response.items[i].url;
                        break;
                    }
                }
                
                // Get session file
                StartCoroutine(GetJSONFromURL(fileUrl, (jsonString) =>
                {
                    ShipObjectWrapper storedData = JsonUtility.FromJson<ShipObjectWrapper>(jsonString);

                    List<string> pods = new();
                    List<string> guns = new();
                    // Updates the specific slot where the ship was bought with the new ship.
                    ShipObject ship = new ShipObject
                    {
                        shipId = "",
                        pods = pods,
                        guns = guns,
                        selected = false
                    };

                    storedData.ships[slot] = ship;
                    storedData.isDead = true;
                    
                    string updatedData = JsonUtility.ToJson(storedData);

                    // Create a temporary file path
                    string filePath = Path.Combine(Application.persistentDataPath, "owned_ships.json");

                    // Write the JSON string to the temporary file
                    File.WriteAllText(filePath, updatedData);
                    var file = File.Open(filePath, FileMode.Open);

                    LootLockerSDKManager.UpdatePlayerFile(fileId, file, response =>
                    {
                        if (response.success)
                        {
                            Debug.Log("Successfully updated player file, url: " + response.url);
                            // Delete the temporary file
                            file.Close();
                            File.Delete(filePath);
                            print("------------- RemoveFromOwnedShipsFile");
                            CockPitMultiplayerManager.Instance.processesToFinish++;
                        }
                        else
                        {
                            Debug.Log("Error updating player file");
                        }
                    });
                }));
            }
            else
            {
                Debug.Log("Error retrieving player storage");
            }
        });
    }

    /// <summary>
    /// This function returns the json text from a specific url that LootLocker provides
    /// </summary>
    private IEnumerator GetJSONFromURL(string url, Action<string> callback)
    {
        using (UnityWebRequest webRequest = UnityWebRequest.Get(url))
        {
            yield return webRequest.SendWebRequest();

            if (webRequest.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("Failed to retrieve JSON from URL: " + webRequest.error);
                callback(null);
            }

            string json = webRequest.downloadHandler.text;
            callback(json);
        }
    }

    /// <summary>
    /// This will be called when we click on the disengage button. This changes our battle mode and disables the blue laser.
    /// </summary>
    public void StopBattle()
    {
        CmdChangeBattleMode(false);

        CockPitMultiplayerManager.Instance.blueLaser.SetActive(false);
    }


    /// <summary>
    /// This function is used to detect the enemy ships upon clicking on them.
    /// </summary>
    private bool IsShipUnderMouse(out BattleMultiplayerManager shipManager)
    {
        shipManager = null;

        PointerEventData eventData = new(EventSystem.current)
        {
            position = Input.mousePosition
        };

        List<RaycastResult> results = new();

        GraphicRaycaster raycaster = FindObjectOfType<Canvas>().GetComponent<GraphicRaycaster>();
        raycaster.Raycast(eventData, results);

        foreach (RaycastResult result in results)
        {
            shipManager = result.gameObject.GetComponent<BattleMultiplayerManager>();
            if (shipManager != null)
                return true;
        }

        return false;
    }


    /// <summary>
    /// Just to know which ship we have clicked upon.
    /// </summary>
    [TargetRpc]
    private void TargetPrintClickedShipName(NetworkConnection target, string shipName) => Debug.Log("Clicked on ship with name: " + shipName);


    /// <summary>
    /// Just to know the player name of the ship we have clicked on.
    /// </summary>
    [TargetRpc]
    private void TargetPrintClickedPlayerName(NetworkConnection target, string playerName) => Debug.Log("Clicked on ship with player name: " + playerName);


    /// <summary>
    /// Just a local function that calculates gun data and passes it on the network.
    /// </summary>
    private void CalculateGunData(Data data)
    {
        data.weaponDatas.ForEach(x =>
        {
            GunsMultiplayer newGun = new()
            {
                gunName = x.weapon_Name,
                accuracy = x.data[0].value,
                firingRate = x.data[1].value,
                damage = x.data[2].value
            };

            CmdShowGunsData(newGun);
        });
    }


    /// <summary>
    /// This will be called when the player clicks on the Engage button.
    /// </summary>
    public void OnEngageButtonClicked()
    {
        if (targetShipIdentity != null)
        {
            CmdEngageShip(targetShipIdentity);

            StartCoroutine(nameof(DisableScanAnimationAndDisplayWifi));
        }
    }


    /// <summary>
    /// This will be called when the player clicks on the DisEngage button.
    /// </summary>
    public void OnDisEngageButtonClicked()
    {
        if (engagedWith != null)
        {
            DisableUI();
            foreach (var player in engagedWith.GetComponent<BattleMultiplayerManager>().attackingPlayer)
            {
                if (engagedWith.name.Equals(player.engagedWith.name))
                {
                    engagedWith.transform.GetChild(0).gameObject.SetActive(false);
                    engagedWith.transform.GetChild(1).gameObject.SetActive(false);
                    engagedWith.transform.GetChild(3).gameObject.SetActive(false);
                    engagedWith.transform.GetChild(4).gameObject.SetActive(false);
                }
            }

            CmdDisEngageShip(engagedWith);
        }
    }


    /// <summary>
    /// This will be called to stop the scan animation when we diengage.
    /// </summary>
    private IEnumerator DisableScanAnimationAndDisplayWifi()
    {
        yield return new WaitUntil(() => engagedWith != null);

        engagedWith.GetComponent<Image>().enabled = true;
        engagedWith.transform.GetChild(1).gameObject.SetActive(false);
        engagedWith.transform.GetChild(4).gameObject.SetActive(true);
    }


    /// <summary>
    /// Called on on pointer enter on ships.
    /// </summary>
    public void EnableUI()
    {
        // This is common for both animation and enagaged with.
        transform.GetChild(0).gameObject.SetActive(true);
        transform.GetChild(3).gameObject.SetActive(true);

        if (CockPitMultiplayerManager.Instance.localPlayer.engagedWith != null && 
            CockPitMultiplayerManager.Instance.localPlayer.engagedWith.name == gameObject.name)
        {
            GetComponent<Image>().enabled = true;

            transform.GetChild(1).gameObject.SetActive(false);
            transform.GetChild(4).gameObject.SetActive(true);
        }

        else
        {
            GetComponent<Image>().enabled = false;

            transform.GetChild(1).gameObject.SetActive(true);
        }

        CockPitMultiplayerManager.Instance.shipAnimatorController.ForEach(x =>
        {
            string shipName = GetComponent<Image>().sprite.name;

            if (shipName[5..].Equals(x.name) || shipName.Equals(x.name) || shipName[6..].Equals(x.name))
                transform.GetChild(1).GetComponent<Animator>().runtimeAnimatorController = x;
        });

        if (GetComponent<Image>().sprite.name[..5].Equals("Front"))
            transform.GetChild(1).GetComponent<Animator>().Play("Scan 2", 0);

        if (GetComponent<Image>().sprite.name[..4].Equals("Left"))
            transform.GetChild(1).GetComponent<Animator>().Play("Scan 1", 0);

        if (GetComponent<Image>().sprite.name[..5].Equals("Class"))
        {
            GetComponent<Image>().enabled = true;

            transform.GetChild(1).gameObject.SetActive(false);
        }

        CockPitMultiplayerManager.Instance.engagedWithShipImage.color = new(1, 1, 1, 1);

        CockPitMultiplayerManager.Instance.engagedWithShipName.text = playerName;

        CockPitMultiplayerManager.Instance.engagedWithShipHealthBar.transform.parent.gameObject.SetActive(true);

        if(damageRatio == 0)
        {
            CockPitMultiplayerManager.Instance.engagedWithShipHealthBar.size = 1;
            transform.GetChild(3).GetChild(0).GetComponent<Scrollbar>().size = 1;
        }
        else
        {
            CockPitMultiplayerManager.Instance.engagedWithShipHealthBar.size = damageRatio;
            transform.GetChild(3).GetChild(0).GetComponent<Scrollbar>().size = damageRatio;
        }
    }


    /// <summary>
    /// Called on on pointer exit on ships.
    /// </summary>
    public void DisableUI()
    {
        // If this is the local player, skip the image enabling step
        if (!isLocalPlayer)
            GetComponent<Image>().enabled = true;

        transform.GetChild(0).gameObject.SetActive(false);
        transform.GetChild(1).gameObject.SetActive(false);
        transform.GetChild(3).gameObject.SetActive(false);
        transform.GetChild(4).gameObject.SetActive(false);

        CockPitMultiplayerManager.Instance.engagedWithShipImage.sprite = null;
        CockPitMultiplayerManager.Instance.engagedWithShipImage.color = new(1, 1, 1, 0);

        CockPitMultiplayerManager.Instance.engagedWithShipName.text = null;

        CockPitMultiplayerManager.Instance.engagedWithShipHealthBar.transform.parent.gameObject.SetActive(false);
    }


    /// <summary>
    /// This is used to check if the ConfirmPilot button is selected.
    /// </summary>
    private bool IsEngageButtonClicked()
    {
        if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null)
        {
            GameObject clickedObject = EventSystem.current.currentSelectedGameObject;
            return clickedObject == CockPitMultiplayerManager.Instance.engageButton;
        }

        return false;
    }


    /// <summary>
    /// This is a call from server to all clients telling someone left, check ho has and do the necessary work.
    /// </summary>
    [ClientRpc]
    public void RpcNotifyAboutDisconnection()
    {
        if (isLocalPlayer)
        {
            if (engagedWith == null)
            {
                CockPitMultiplayerManager.Instance.blueLaser.SetActive(false);
                CockPitMultiplayerManager.Instance.engageButton.SetActive(true);
                CockPitMultiplayerManager.Instance.engageButton.GetComponent<Button>().interactable = false;
                CockPitMultiplayerManager.Instance.disEngageButton.SetActive(false);
            }
        }
    }


    /// <summary>
    /// This works when we click on another ship so it disable all ships animations first.
    /// </summary>
    public void DeSelectAllShips()
    {
        for (int i = 0; i < CockPitMultiplayerManager.Instance.enemyShipsParent.transform.childCount; i++)
        {
            CockPitMultiplayerManager.Instance.enemyShipsParent.transform.GetChild(i).
                GetComponent<BattleMultiplayerManager>().DisableUI();
        }
    }


    /// <summary>
    /// This is a hook that hets called always when a new player attacks or stops attacking us.
    /// </summary>
    private void OnIsUnderAttackChanged(int oldValue, int newValue)
    {
        if (!isLocalPlayer) return;

        if (newValue > oldValue)
        {
            CockPitMultiplayerManager.Instance.StartLaserRotator("Receiving");

            StartCoroutine(EnableDisableRedShipImage("Red"));
        }

        if (newValue < oldValue)
        {
            CockPitMultiplayerManager.Instance.StartLaserRotator("Disable");

            StartCoroutine(EnableDisableRedShipImage("Front"));
        }
    }


    /// <summary>
    /// This is called whenever damage ratio changes.
    /// </summary>
    private void OnUpdateUI(float oldValue, float newValue)
    {
        if(!isLocalPlayer) return;

        if(!CockPitMultiplayerManager.Instance.useAllDataTest)
            SetLootlockerShipHP(data.slot, damageRatio);

        CockPitMultiplayerManager.Instance.playerHP.fillAmount = damageRatio;

        if((int)damageRatio * maxHull >= 0)
            CockPitMultiplayerManager.Instance.playerHealth.text = ((int)(damageRatio * maxHull)).ToString();
    }


    /// <summary>
    /// This is used to sync the latest health in the multiplayer scene too.
    /// </summary>
    public void SetLootlockerShipHP(int slot, float health)
    {
        // Ship Slot Health Leaderboards. Use for storing ships health, each one corresponds to a slot.
        List<string> lootlockerStoreNames = new List<string> {"17059", "17061", "17062", "17063"};

        if(health >= 0.01f)
        {
            LootLockerSDKManager.SubmitScore("20", (int)MathF.Floor(health * 100), lootlockerStoreNames[slot], (response) => {
                Debug.Log("Succesfully uploaded score");
                data.shipHealth = Mathf.Floor(health * 100.0f) / 100.0f; // This is neccessary for the healthbar not to jump to another value when going back to home planet
            });
        }

        if(health == 999){
            print("------------- SetLootlockerShipHP");
            CockPitMultiplayerManager.Instance.processesToFinish++;
        }
    }


    /// <summary>
    /// This changes ship images which shows their states.
    /// </summary>
    private IEnumerator EnableDisableRedShipImage(string value)
    {
        yield return new WaitForSeconds(0.05f);

        if(value == "Red")
        {
            allPlayers.ForEach(p =>
            {
                if (p.engagedWith != null && p.gameObject.name != gameObject.name)
                {
                    string shipName = p.GetComponent<Image>().sprite.name;

                    data.redShipImages.ForEach(x =>
                    {
                        if (shipName[5..].Equals(x.name) || shipName[6..].Equals(x.name))
                        {
                            p.GetComponent<Image>().sprite = x;
                        }
                    });
                }
            });
        }

        else if (value == "Front")
        {
            allPlayers.ForEach(p =>
            {
                if (p.engagedWith != null && p.gameObject.name != gameObject.name && !p.battleMode)
                {
                    string shipName = p.engagedWith.GetComponent<Image>().sprite.name;

                    data.frontShipImages.ForEach(x =>
                    {
                        if (shipName[5..].Equals(x.name[6..]))
                        {
                            p.GetComponent<Image>().sprite = x;
                        }
                    });
                }
            });
        }
    }


    /// <summary>
    /// This always keep track of player who are attacking other clients so that we can show lasers from one to another.
    /// </summary>
    private IEnumerator MonitorPlayerBattles()
    {
        while (true)
        {
            foreach (BattleMultiplayerManager player in allPlayers)
            {
                if (GetComponent<NetworkIdentity>().netId != player.netId)
                {
                    if (player.engagedWith != null && player.engagedWith.name != gameObject.name)
                    {
                        StartCoroutine(ShowEnemyToEnemyLasers(player.engagedWith.gameObject, player.gameObject));
                    }
                }
            }

            yield return new WaitForSeconds(1f);
        }
    }


    /// <summary>
    /// This code calculate from one ship to another ship, so that we can have to show lasers from a 3rd perspective.
    /// </summary>
    private IEnumerator ShowEnemyToEnemyLasers(GameObject to, GameObject from)
    {
        if (to == null || from == null) yield break;

        yield return new WaitForSeconds(1f);

        for (int i = 0; i < allPlayers.Count; i++)
        {
            if (allPlayers[i] == null || !allPlayers[i].gameObject.activeInHierarchy) continue;

            if (from.name.Equals(allPlayers[i].name))
            {
                if (from.transform.childCount <= 5 && from.GetComponent<BattleMultiplayerManager>().battleMode == true)
                {
                    GameObject LaserPrefab = Instantiate(CockPitMultiplayerManager.Instance.redLaser, from.transform.position, Quaternion.identity);

                    LaserPrefab.transform.SetParent(allPlayers[i].transform);

                    Vector2 fireDirection = from.transform.position - to.transform.position;

                    LaserPrefab.GetComponent<RectTransform>().sizeDelta =
                        new Vector2(60, Vector3.Distance(to.transform.localPosition,
                        from.transform.localPosition));

                    LaserPrefab.transform.rotation = Quaternion.LookRotation(Vector3.forward,
                        fireDirection);
                }
            }

            if (allPlayers[i].transform.childCount > 5 && allPlayers[i].GetComponent<BattleMultiplayerManager>().battleMode == false)
            {
                Transform child = allPlayers[i].transform.GetChild(5);

                if (child != null)
                {
                    Destroy(child.gameObject);
                }
            }
        }
    }


    /// <summary>
    /// This makes the engaged ship null after we disengage so that we can again battle. 
    /// </summary>
    private IEnumerator NullEngageShip()
    {
        yield return new WaitForSeconds(0.1f);

        engagedWith = null;
    }


    /// <summary>
    /// Calcues the average firing rate depending on how much guns we have.
    /// </summary>
    private float CalculateTotalFiringRate(Data data)
    {
        if (data.weaponDatas.Count == 0)
            return 0;

        float totalFiringRate = 0;
        data.weaponDatas.ForEach(gun => totalFiringRate += gun.data[1].value);

        // Calculate the average by dividing the total by the number of guns.
        float averageFiringRate = totalFiringRate / data.weaponDatas.Count;

        return averageFiringRate;
    }


    /// <summary>
    /// Called when Mouse hover over the UI.
    /// </summary>
    public void OnHover()
    {
        Cursor.SetCursor(CockPitMultiplayerManager.Instance.redCursor,
            new Vector2(CockPitMultiplayerManager.Instance.redCursor.width / 2, CockPitMultiplayerManager.Instance.redCursor.height / 2), CursorMode.Auto);
    }


    /// <summary>
    /// Called when Mouse un-hover over the UI.
    /// </summary>
    public void OnUnHover()
    {
        Cursor.SetCursor(CockPitMultiplayerManager.Instance.blueCrosshair,
            new Vector2(CockPitMultiplayerManager.Instance.blueCrosshair.width / 2, CockPitMultiplayerManager.Instance.blueCrosshair.height / 2), CursorMode.Auto);
    }


    /// <summary>
    /// This will let us disconnect once we are dead.
    /// </summary>
    public void Disconnect() => NetworkClient.Disconnect();


    #endregion
}