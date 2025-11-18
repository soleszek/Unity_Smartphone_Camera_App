using UnityEngine;
using UnityEngine.UI;
using System;
using System.Collections;
using System.IO;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;
using TMPro;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class CameraCaptureMQTT : MonoBehaviour
{
    public string mqttBrokerAddress = "192.168.137.1";
    public int mqttPort = 1883;
    public string topic = "cam/new_image";
    public string arduinoTopic = "traffic/status"; 
    public Image captureIndicator;  

    // selectionTopic default set to cam/conditions
    public string selectionTopic = "cam/conditions";
    public TMP_Dropdown dropdownWeather; // Dry / Rainy
    public TMP_Dropdown dropdownLight;   // Day / Night
    public TMP_Dropdown dropdownTraffic; // Off-peak hours / Rush hours

    private WebCamTexture webcam;
    private MqttClient client;
    private bool mqttConnected = false;
    private RawImage rawImage;

    private long arduinoTime = 0; 

    private long lastTa = 0;     
    private long lastTrx = 0;    
    private bool captureRequested = false; 

    private int arduinoMsgCounter = 0;


    void Start()
    {
        rawImage = GetComponent<RawImage>();
        if (rawImage == null)
        {
            Debug.LogError("❌ Obiekt musi mieć komponent RawImage!");
            return;
        }

        try
        {
            client = new MqttClient(mqttBrokerAddress, mqttPort, false, null, null, MqttSslProtocols.None);
            client.MqttMsgPublishReceived += OnMqttMessageReceived;
            client.Connect(Guid.NewGuid().ToString());
            client.Subscribe(new string[] { arduinoTopic }, new byte[] { MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE });
            mqttConnected = true;
            Debug.Log("✅ MQTT connected and subscribed to Arduino topic");

            // Dodane: listeners publikujące selekcję niezależnie od robienia zdjęć
            if (dropdownWeather != null) dropdownWeather.onValueChanged.AddListener(_ => PublishSelection());
            if (dropdownLight != null) dropdownLight.onValueChanged.AddListener(_ => PublishSelection());
            if (dropdownTraffic != null) dropdownTraffic.onValueChanged.AddListener(_ => PublishSelection());
            // opcjonalnie opublikuj aktualny stan natychmiast:
            PublishSelection();
        }
        catch (Exception ex)
        {
            Debug.LogError("❌ MQTT connection failed: " + ex.Message);
        }

        string camName = "";
        if (WebCamTexture.devices.Length == 0)
        {
            Debug.LogError("❌ Nie znaleziono żadnej kamery!");
            return;
        }

        foreach (var d in WebCamTexture.devices)
        {
            if (!d.isFrontFacing)
            {
                camName = d.name;
                break;
            }
        }
        if (string.IsNullOrEmpty(camName))
        {
            camName = WebCamTexture.devices[0].name;
        }

        webcam = new WebCamTexture(camName);
        rawImage.texture = webcam;
        webcam.Play();

        if (captureIndicator != null)
            captureIndicator.enabled = false; // Domyślnie ukryta

        Debug.Log($"✅ Kamera uruchomiona: {webcam.deviceName}");
    }

    private void OnMqttMessageReceived(object sender, MqttMsgPublishEventArgs e)
    {
        if (e.Topic == arduinoTopic)
        {
            try
            {
                string msg = System.Text.Encoding.UTF8.GetString(e.Message);
                int idx = msg.IndexOf("\"t\":");
                if (idx != -1)
                {
                    int endIdx = msg.IndexOf(",", idx);
                    string timeStr = msg.Substring(idx + 4, endIdx - (idx + 4));
                    if (long.TryParse(timeStr, out long parsedTime))
                    {
                        arduinoTime = parsedTime;
                        lastTa = arduinoTime;
                        lastTrx = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                        arduinoMsgCounter++;

                        if (arduinoMsgCounter % 5 == 0)
                        {
                            captureRequested = true;
                        }

                        Debug.Log($"🕒 Received Arduino time: {arduinoTime}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("❌ Error parsing Arduino data: " + ex.Message);
            }
        }
    }

    void Update()
    {
        if (captureRequested)
        {
            captureRequested = false;
            StartCoroutine(CaptureAndSend());
        }
    }


    IEnumerator CaptureAndSend()
    {
        if (captureIndicator != null)
            captureIndicator.enabled = true;

        yield return new WaitForEndOfFrame();

        Texture2D photo = new Texture2D(webcam.width, webcam.height, TextureFormat.RGB24, false);
        photo.SetPixels(webcam.GetPixels());
        photo.Apply();

        byte[] bytes = photo.EncodeToJPG();

        string timestampISO = DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff");
        string fileName = $"{timestampISO}.jpg";
        string path = Path.Combine(Application.persistentDataPath, fileName);
        File.WriteAllBytes(path, bytes);
        Debug.Log($"📸 Captured: {path}");

        long Treal = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long Tphoto = Treal; 

        byte[] fileData = File.ReadAllBytes(path);
        var form = new System.Collections.Generic.List<UnityEngine.Networking.IMultipartFormSection>();
        form.Add(new UnityEngine.Networking.MultipartFormFileSection("file", fileData, fileName, "image/jpeg"));
        var www = UnityEngine.Networking.UnityWebRequest.Post("http://192.168.137.1:5000/upload", form);

        yield return www.SendWebRequest();

        try
        {
            if (www.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                Debug.LogError("❌ Błąd wysyłania: " + www.error);
            else
                Debug.Log("✅ Wysłano zdjęcie na serwer Flask");
        }
        catch (Exception ex)
        {
            Debug.LogError("❌ Wyjątek podczas wysyłania: " + ex.Message);
        }

        if (mqttConnected)
        {
            string message =
                $"{{\"file\":\"{fileName}\",\"timestamp\":{lastTrx},\"Ta\":{arduinoTime},\"Tphoto\":{Tphoto}}}";

            client.Publish(topic, System.Text.Encoding.UTF8.GetBytes(message));
            Debug.Log($"📡 Published to MQTT: {message}");
        }

        Destroy(photo);

        if (captureIndicator != null)
            captureIndicator.enabled = false;
    }

    private void PublishSelection()
{
    if (!mqttConnected || client == null || string.IsNullOrEmpty(selectionTopic)) return;

    // 1: d/n  (day/night) - dropdownLight
    char dayNight = 'd';
    if (dropdownLight != null && dropdownLight.options != null && dropdownLight.options.Count > 0)
    {
        string txt = dropdownLight.options[dropdownLight.value].text;
        dayNight = (txt == "Night") ? 'n' : 'd';
    }

    // 2: d/r  (dry/rain) - dropdownWeather
    char dryRain = 'd';
    if (dropdownWeather != null && dropdownWeather.options != null && dropdownWeather.options.Count > 0)
    {
        string txt = dropdownWeather.options[dropdownWeather.value].text;
        dryRain = (txt == "Rainy") ? 'r' : 'd';
    }

    // 3: r/o  (rush/off-peak) - dropdownTraffic
    char rushOff = 'o';
    if (dropdownTraffic != null && dropdownTraffic.options != null && dropdownTraffic.options.Count > 0)
    {
        string txt = dropdownTraffic.options[dropdownTraffic.value].text;
        rushOff = (txt == "Rush hours") ? 'r' : 'o';
    }

    // trzyliterowy kod b: [0]=d/n, [1]=d/r, [2]=r/o
    string b = new string(new[] { dayNight, dryRain, rushOff });

    // JSON w formacie uzgodnionym z Arduino: {"b":"ddr"}
    string payload = "{\"b\":\"" + b + "\"}";

    try
    {
        client.Publish(selectionTopic, System.Text.Encoding.UTF8.GetBytes(payload));
        Debug.Log($"📡 Published selection to {selectionTopic}: {payload}");
    }
    catch (Exception ex)
    {
        Debug.LogError("❌ Selection publish failed: " + ex.Message);
    }
}

#if UNITY_EDITOR
    // Ensure Inspector shows cam/conditions (fix existing serialized instances that still show cam/selection).
    // Reset() runs when component is first added or Reset is used in Inspector.
    // OnValidate() runs in editor after changes/compilation; it corrects old value "cam/selection".
    void Reset()
    {
        selectionTopic = "cam/conditions";
        EditorUtility.SetDirty(this);
    }

    void OnValidate()
    {
        if (selectionTopic == "cam/selection")
        {
            selectionTopic = "cam/conditions";
            EditorUtility.SetDirty(this);
        }
    }
#endif
}