using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
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
    public float previewScale = 0.2f;
    [Tooltip("Maximum distance (squared) to point such that thumbnail is still displayed")]
    public float maximumDistanceSquared = 0.01f;

    private List<(string id, Vector3 position)> _points;
    private string _lastSelected = "";

    private void Start()
    {
      var items = LoadFeatures("features");
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

      var (id, sqrDistance, itemPosition) = _points
        .Select(item => (item.id, (item.position - position).sqrMagnitude, item.position))
        .Aggregate((a, b) => a.sqrMagnitude > b.sqrMagnitude ? b : a);

      previewText.text = sqrDistance.ToString(CultureInfo.InvariantCulture);

      previewImage.enabled = sqrDistance < maximumDistanceSquared;
      
      UpdatePreviewPosRot(itemPosition);

      if (id == _lastSelected)
        return;

      StartCoroutine(DownloadTexture($"http://10.34.58.72:8080/thumbnails/i_{id[..^2]}/i_{id}.jpg", id, itemPosition,
        OnDownloadSuccess));
      // StartCoroutine(DownloadTexture(
      //   $"http://sipi.participatory-archives.ch/SGV_10/{id[..^2]}.jp2/full/256,/0/default.jpg", id, itemPosition,
      //   OnDownloadSuccess));

      _lastSelected = id;
    }

    private static List<(string id, Vector3 position)> LoadFeatures(string filePath)
    {
      var data = Resources.Load<TextAsset>(filePath).text;

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

      // Length of the largest size of the bounding box
      var normalizer = Mathf.Max(xMax - xMin, yMax - yMin, zMax - zMin);

      // Center of the bounding box
      var center = new Vector3(xMin + xMax, yMin + yMax, zMin + zMax) / 2;

      return points.Select(point => (point.id, (point.position - center) / normalizer)).ToList();
    }

    /// <summary>
    /// Downloads the image from the given URL and transforms it into a texture.
    /// 
    /// Checks if this texture is still current using the provided ID.
    /// </summary>
    /// <param name="url">URL of the image to use as texture</param>
    /// <param name="id">ID of the image to be downloaded for checking relevance</param>
    /// <param name="itemPosition">Position of the item in local space</param>
    /// <param name="onSuccess">Function to call when successfully downloaded</param>
    private static IEnumerator DownloadTexture(string url, string id, Vector3 itemPosition,
      Action<Texture2D, string, Vector3> onSuccess)
    {
      using var www = UnityWebRequestTexture.GetTexture(url);
      yield return www.SendWebRequest();

      if (www.result is UnityWebRequest.Result.ConnectionError or UnityWebRequest.Result.ProtocolError)
      {
        Debug.LogError($"URL: {url}\nError: {www.error}");
      }
      else
      {
        var loadedTexture = ((DownloadHandlerTexture) www.downloadHandler).texture;
        onSuccess(loadedTexture, id, itemPosition);
      }
    }

    private void OnDownloadSuccess(Texture2D loadedTexture, string id, Vector3 itemPosition)
    {
      // Set texture
      previewImage.material.mainTexture = loadedTexture;
      // Adjust aspect ratio
      float factor = Mathf.Max(loadedTexture.width, loadedTexture.height);
      var scale = new Vector3(loadedTexture.width / factor, loadedTexture.height / factor, 1);
      var t = previewImage.transform;
      t.localScale = scale * previewScale;
      UpdatePreviewPosRot(itemPosition);
    }

    private void UpdatePreviewPosRot(Vector3 itemPosition)
    {
      var t = previewImage.transform;
      // Set position
      t.position = transform.TransformPoint(itemPosition) + Vector3.up * (t.localScale.y / 2);
      // Rotate towards camera
      if (Camera.main == null) return;
      var forwardVector = t.position - Camera.main.transform.position;
      forwardVector.y = 0;
      t.rotation = Quaternion.LookRotation(forwardVector);
    }
  }
}