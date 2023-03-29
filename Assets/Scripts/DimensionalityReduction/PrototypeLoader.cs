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

  public enum Server
  {
    Maas,
    Sipi
  }

  public enum Coloration
  {
    White,
    Coordinates
  }

  public class PrototypeLoader : MonoBehaviour
  {
    public ParticleSystem system;
    public Transform previewPosition;
    public TextMesh previewText;
    public Renderer previewImage;
    public float previewScale = 0.2f;
    public Coloration startColor;

    [Tooltip("Maximum distance (squared) to point such that thumbnail is still displayed")]
    public float maximumDistanceSquared = 0.01f;

    private List<(string id, Vector3 position)> _points;
    private string _lastSelected = "";

    // Interaction variables
    private Dictionary<Transform, Vector3> _activeInteractors = new();
    private Camera _camera;

    // Config
    private Server _dataServer = Server.Maas;

    private void Start()
    {
      _camera = Camera.main;
      var items = LoadFeatures("features");
      items = NormalizeToBoundingBox(items);

      var mainConfig = system.main;
      mainConfig.maxParticles = items.Count;

      _points = items;

      EmitParticles(startColor);
    }

    private void Update()
    {
      UpdateInteraction();
    }

    private void FixedUpdate()
    {
      UpdatePreview();
    }

    public void OnInteraction(Transform interactor, bool start)
    {
      if (start)
      {
        _activeInteractors.Add(interactor, interactor.position);
      }
      else
      {
        _activeInteractors.Remove(interactor);
      }
    }

    public void SetMaasServer()
    {
      _dataServer = Server.Maas;
    }

    public void SetSipiServer()
    {
      _dataServer = Server.Sipi;
    }

    public void SetColorWhite()
    {
      EmitParticles(Coloration.White);
    }

    public void SetColorCoordinates()
    {
      EmitParticles(Coloration.Coordinates);
    }

    private void EmitParticles(Coloration coloration)
    {
      system.Clear();

      switch (coloration)
      {
        case Coloration.White:
          foreach (var emitParams in _points.Select(item => new ParticleSystem.EmitParams
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

          break;
        case Coloration.Coordinates:
          foreach (var emitParams in _points.Select(item => new ParticleSystem.EmitParams
                   {
                     position = item.position,
                     velocity = Vector3.zero,
                     startLifetime = float.PositiveInfinity,
                     startSize = .01f,
                     startColor = new Color(item.position.x + 0.5f, item.position.y + 0.5f, item.position.z + 0.5f)
                   }))
          {
            system.Emit(emitParams, 1);
          }

          break;
        default:
          throw new ArgumentOutOfRangeException(nameof(coloration), coloration, null);
      }
    }

    private void UpdateInteraction()
    {
      if (_activeInteractors.Count != 2) return;

      var positions = _activeInteractors.Keys.Select(key => (_activeInteractors[key], key.position)).ToArray();

      var (old0, new0) = positions.First();
      var (old1, new1) = positions.Last();

      var t = transform;
      var scalingFactor = (new0 - new1).magnitude / (old0 - old1).magnitude;
      // Perform scaling at the center point of the gesture
      var scalingPoint = (old0 + old1) / 2 - t.position;

      t.localScale *= scalingFactor;
      t.position += scalingPoint - scalingPoint * scalingFactor;

      var keys = _activeInteractors.Keys.ToArray();
      foreach (var key in keys)
      {
        _activeInteractors[key] = key.position;
      }
    }

    /// <summary>
    /// Determine which point is closest to the preview position and start the necessary coroutine to download the
    /// appropriate texture.
    /// Also updates preview location and rotation.
    /// </summary>
    private void UpdatePreview()
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

      var thumbnailURL = GetThumbnailURL(id);

      StartCoroutine(DownloadTexture(thumbnailURL, id, itemPosition, OnDownloadSuccess));

      _lastSelected = id;
    }

    private string GetThumbnailURL(string id)
    {
      return _dataServer switch
      {
        Server.Maas => $"http://10.34.58.72:8080/thumbnails/i_{id[..^2]}/i_{id}.jpg",
        Server.Sipi => $"https://sipi.participatory-archives.ch/SGV_10/{id[..^2]}.jp2/full/256,/0/default.jpg",
        _ => throw new ArgumentOutOfRangeException()
      };
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
      var forwardVector = t.position - _camera.transform.position;
      forwardVector.y = 0;
      t.rotation = Quaternion.LookRotation(forwardVector);
    }
  }
}