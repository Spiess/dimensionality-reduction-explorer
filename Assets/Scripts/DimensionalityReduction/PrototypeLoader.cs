using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace DimensionalityReduction
{
  [Serializable]
  public class FeatureEntry
  {
    public string id;
    public float x;
    public float y;
    public float z;
  }

  [Serializable]
  public class FeatureFile
  {
    public FeatureEntry[] values;
  }

  public class PrototypeLoader : MonoBehaviour
  {
    public ParticleSystem system;
    public Transform previewPosition;
    public TextMesh previewText;
    public Renderer previewImage;

    private List<(string id, Vector3 position)> _points;
    private string _lastSelected = "";

    private void Start()
    {
      var items = LoadFeatures("Assets/features.json");
      items = NormalizeToBoundingBox(items);

      var mainConfig = system.main;
      mainConfig.maxParticles = items.Count;

      foreach (var emitParams in items.Select(item => new ParticleSystem.EmitParams
               {
                 position = item.position,
                 velocity = Vector3.zero,
                 startLifetime = float.PositiveInfinity,
                 startSize = .01f,
                 startColor = Color.white
               }))
      {
        system.Emit(emitParams, 1);
      }

      _points = items;
    }

    private void FixedUpdate()
    {
      var position = transform.InverseTransformPoint(previewPosition.position);

      var (id, sqrDistance) = _points.Select(item => (item.id, (item.position - position).sqrMagnitude))
        .Aggregate((a, b) => a.sqrMagnitude > b.sqrMagnitude ? b : a);

      previewText.text = sqrDistance.ToString();

      if (id == _lastSelected)
        return;

      StartCoroutine(DownloadTexture($"http://10.34.58.72:8080/thumbnails/i_{id[..^2]}/i_{id}.jpg", id,
        OnDownloadSuccess));

      _lastSelected = id;
    }

    private static List<(string id, Vector3 position)> LoadFeatures(string filePath)
    {
      var data = File.ReadAllText(filePath);

      var featureFile = JsonUtility.FromJson<FeatureFile>(data);

      return featureFile.values.Select(i => (i.id, new Vector3(i.x, i.y, i.z))).ToList();
    }

    /// <summary>
    /// Normalizes points to fit into a cube of side length 1 centered around the origin.
    /// </summary>
    private static List<(string id, Vector3 position)> NormalizeToBoundingBox(
      List<(string id, Vector3 position)> points)
    {
      var positions = points.Select(item => item.position).ToArray();
      var x = positions.Select(point => point.x).ToArray();
      var y = positions.Select(point => point.y).ToArray();
      var z = positions.Select(point => point.z).ToArray();
      var xMin = x.Min();
      var xMax = x.Max();
      var yMin = y.Min();
      var yMax = y.Max();
      var zMin = z.Min();
      var zMax = z.Max();

      var normalizer = Mathf.Max(xMax - xMin, yMax - yMin, zMax - zMin);

      var center = new Vector3(xMin + xMax, yMin + yMax, zMin + zMax) / 2;

      return points.Select(point => (point.id, (point.position - center) / normalizer)).ToList();
    }

    private static IEnumerator DownloadTexture(string url, string id, Action<Texture2D, string> onSuccess)
    {
      using var www = UnityWebRequestTexture.GetTexture(url);
      yield return www.SendWebRequest();

      if (www.result is UnityWebRequest.Result.ConnectionError or UnityWebRequest.Result.ProtocolError)
      {
        Debug.LogError($"URL: {url}\nError: {www.error}");
      }
      else
      {
        var loadedTexture = ((DownloadHandlerTexture)www.downloadHandler).texture;
        onSuccess(loadedTexture, id);
      }
    }

    private void OnDownloadSuccess(Texture2D loadedTexture, string id)
    {
      previewImage.material.mainTexture = loadedTexture;
      float factor = Mathf.Max(loadedTexture.width, loadedTexture.height);
      var scale = new Vector3(loadedTexture.width / factor, loadedTexture.height / factor, 1);
      previewImage.transform.localScale = scale;
    }
  }
}