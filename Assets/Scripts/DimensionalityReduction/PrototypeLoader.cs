using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SimpleInteractionSystem;
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
    public Renderer previewPrefab;
    public float previewScale = 0.2f;
    public Coloration startColor;

    [Tooltip("Maximum distance (squared) to point such that thumbnail is still displayed")]
    public float maximumDistanceSquared = 0.01f;

    [Header("Randomized Previews")] public bool enableRandomizedPreviews = true;

    [Tooltip("Minimum distance between previews to control sparsity")]
    public float minimumPreviewDistance = .18f;

    public float randomizedPreviewScale = .1f;
    public float hideRandomizedPreviewDistanceSquared = 0.02f;

    private Dictionary<Vector3, Renderer> _randomizedPreviews = new();

    private List<(string id, Vector3 position)> _points;

    // Interaction variables
    private Dictionary<Transform, Vector3> _activeInteractors = new();
    private Camera _camera;

    // Config
    private Server _dataServer = Server.Sipi;

    // For tracking the interactors currently inside the bounding box and the last segment data they hovered.
    private readonly Dictionary<Interactor, string> _enteredLastId = new();
    private readonly Dictionary<Interactor, Renderer> _enteredPreviews = new();

    private void Start()
    {
      _camera = Camera.main;
      var items = LoadFeatures("features");
      items = NormalizeToBoundingBox(items);

      var mainConfig = system.main;
      mainConfig.maxParticles = items.Count;

      _points = items;

      EmitParticles(startColor);

      if (enableRandomizedPreviews)
        GeneratePreviews();
    }

    private void GeneratePreviews()
    {
      // Destroy existing previews
      foreach (var (_, preview) in _randomizedPreviews)
      {
        Destroy(preview.gameObject);
      }

      _randomizedPreviews.Clear();

      // Randomly choose points to display previews for
      var orderedPoints = _points.OrderBy(_ => UnityEngine.Random.value).ToList();

      var previewPoints = new List<(string id, Vector3 position)>();

      var sqrMinimumPreviewDistance = minimumPreviewDistance * minimumPreviewDistance;

      foreach (var (segment, position) in orderedPoints)
      {
        if (previewPoints.Count == 0)
        {
          previewPoints.Add((segment, position));
          continue;
        }

        var minSqrDistance = previewPoints.Select(pair => (pair.position - position).sqrMagnitude).Min();

        // Ignore points too close to existing previews
        if (minSqrDistance < sqrMinimumPreviewDistance)
          continue;

        previewPoints.Add((segment, position));
      }

      // Create previews
      _randomizedPreviews = previewPoints.Select(pair =>
      {
        var preview = Instantiate(previewPrefab);
        preview.transform.localScale = previewScale * Vector3.one;

        return (preview, pair.position);
      }).ToDictionary(pair => pair.position, pair => pair.preview);

      // Start texture download (must occur after previews are created)
      foreach (var (id, position) in previewPoints)
      {
        var thumbnailURL = GetThumbnailURL(id);

        StartCoroutine(DownloadTexture(thumbnailURL, id, position, null, OnDownloadSuccessRandomizedPreview));
      }
    }

    private void Update()
    {
      UpdateInteraction();
    }

    private void FixedUpdate()
    {
      UpdatePreviews();
    }
    
    private void OnTriggerEnter(Collider other)
    {
      if (!other.TryGetComponent<Interactor>(out var interactor)) return;

      // Check if interactor had previously entered but not been removed
      if (!_enteredLastId.TryAdd(interactor, null))
      {
        _enteredLastId[interactor] = null;
        return;
      }

      var preview = Instantiate(previewPrefab);
      preview.transform.localScale = previewScale * Vector3.one;
      _enteredPreviews.Add(interactor, preview);
    }

    private void OnTriggerExit(Collider other)
    {
      if (!other.TryGetComponent<Interactor>(out var interactor)) return;

      _enteredLastId.Remove(interactor);

      var preview = _enteredPreviews[interactor];
      Destroy(preview.gameObject);
      _enteredPreviews.Remove(interactor);
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

      // Scale particles by size to allow scaling of point cloud for easier selection and viewing, but do not scale up
      // beyond a limit to prevent small displays becoming crowded
      var size = .01f / Mathf.Max(transform.localScale.x, 1);

      switch (coloration)
      {
        case Coloration.White:
          foreach (var emitParams in _points.Select(item => new ParticleSystem.EmitParams
                   {
                     position = item.position,
                     velocity = Vector3.zero,
                     startLifetime = float.PositiveInfinity,
                     startSize = size,
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
                     startSize = size,
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

      EmitParticles(startColor);
    }

    /// <summary>
    /// Determine which point is closest to the preview position and start the necessary coroutine to download the
    /// appropriate texture.
    /// Also updates preview location and rotation.
    /// </summary>
    private void UpdatePreviews()
    {
      if (_points.Count == 0)
        return;

      // Enable all randomized previews
      _randomizedPreviews.Values.ToList().ForEach(preview => preview.enabled = true);

      // Update interactor previews
      foreach (var interactor in _enteredLastId.Keys.ToList())
      {
        var position = transform.InverseTransformPoint(interactor.transform.position);

        var (id, sqrDistance, itemPosition) = _points
          .Select(item => (item.id, (item.position - position).sqrMagnitude, item.position))
          .Aggregate((a, b) => a.sqrMagnitude > b.sqrMagnitude ? b : a);

        var preview = _enteredPreviews[interactor];

        var interactorClose = sqrDistance < maximumDistanceSquared / transform.localScale.x;

        preview.enabled = interactorClose;

        if (!interactorClose)
          continue;

        // Disable all randomized previews closer to the interactor than the minimum distance
        _randomizedPreviews.Where(pair =>
            (pair.Key - position).sqrMagnitude < hideRandomizedPreviewDistanceSquared / transform.localScale.x)
          .ToList().ForEach(pair => pair.Value.enabled = false);

        UpdatePreviewPosRot(itemPosition, preview.transform);

        if (id == _enteredLastId[interactor])
          continue;

        var thumbnailURL = GetThumbnailURL(id);

        StartCoroutine(DownloadTexture(thumbnailURL, id, itemPosition, interactor, OnDownloadSuccess));

        _enteredLastId[interactor] = id;
      }

      // Update randomized preview positions and rotations
      foreach (var (position, preview) in _randomizedPreviews)
      {
        UpdatePreviewPosRot(position, preview.transform);
      }
    }

    private string GetThumbnailURL(string id)
    {
      return _dataServer switch
      {
        Server.Maas => $"http://10.34.58.72:8080/thumbnails/i_{id[..^2]}/i_{id}.jpg",
        Server.Sipi => $"http://sipi.participatory-archives.ch/SGV_10/{id[..^2]}.jp2/full/256,/0/default.jpg",
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
    /// <param name="interactor">Interactor causing this preview to be shown</param>
    /// <param name="onSuccess">Function to call when successfully downloaded</param>
    private static IEnumerator DownloadTexture(string url, string id, Vector3 itemPosition, Interactor interactor,
      Action<Texture2D, string, Vector3, Interactor> onSuccess)
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
        onSuccess(loadedTexture, id, itemPosition, interactor);
      }
    }

    private void OnDownloadSuccess(Texture2D loadedTexture, string id, Vector3 itemPosition, Interactor interactor)
    {
      if (!_enteredLastId.ContainsKey(interactor) || id != _enteredLastId[interactor])
        return;

      var preview = _enteredPreviews[interactor];
      // Set texture
      preview.material.mainTexture = loadedTexture;
      // Adjust aspect ratio
      float factor = Mathf.Max(loadedTexture.width, loadedTexture.height);
      var scale = new Vector3(loadedTexture.width / factor, loadedTexture.height / factor, 1);
      var t = preview.transform;
      t.localScale = scale * previewScale;
      UpdatePreviewPosRot(itemPosition, t);
    }

    private void OnDownloadSuccessRandomizedPreview(Texture2D loadedTexture, string id, Vector3 itemPosition,
      Interactor _)
    {
      var preview = _randomizedPreviews[itemPosition];
      // Set texture
      preview.material.mainTexture = loadedTexture;
      // Adjust aspect ratio
      float factor = Mathf.Max(loadedTexture.width, loadedTexture.height);
      var scale = new Vector3(loadedTexture.width / factor, loadedTexture.height / factor, 1);
      var t = preview.transform;
      t.localScale = scale * randomizedPreviewScale;
      UpdatePreviewPosRot(itemPosition, t);
    }

    private void UpdatePreviewPosRot(Vector3 itemPosition, Transform previewTransform)
    {
      // Set position
      previewTransform.position =
        transform.TransformPoint(itemPosition) + Vector3.up * (previewTransform.localScale.y / 2);
      // Rotate towards camera
      var forwardVector = previewTransform.position - _camera.transform.position;
      forwardVector.y = 0;
      previewTransform.rotation = Quaternion.LookRotation(forwardVector);
    }
  }
}