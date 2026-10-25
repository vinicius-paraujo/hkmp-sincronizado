using System.Collections;
using System.Collections.Generic;
namespace UnityEngine {
    public struct Vector2 { public float x, y; public Vector2(float x, float y) { this.x = x; this.y = y; } }
    public struct Vector3 { public float x, y, z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; } }
 public class GameObject {
  public string name; public bool activeSelf=true, activeInHierarchy=true, Managed;
  public Transform transform; public SceneManagement.Scene scene;
  public GameObject(string name, SceneManagement.Scene scene) { this.name=name; this.scene=scene; transform=new Transform { gameObject=this }; }
  public void SetActive(bool value) { activeSelf=value; activeInHierarchy=value; }
  public GameObject Child(string name, bool active=true) { var child=new GameObject(name,scene); child.SetActive(active); transform.Children.Add(child.transform); return child; }
 }
 public class Transform : IEnumerable {
  public GameObject gameObject; public string name => gameObject.name; public readonly List<Transform> Children=new List<Transform>();
  public IEnumerator GetEnumerator()=>Children.GetEnumerator();
 }
}
namespace UnityEngine.SceneManagement { public class Scene { public Scene(string name) {} } }
namespace Hkmp.Game.Client.Entity {
 internal static class EntityRegistry { public static bool TryGetEntry(UnityEngine.GameObject obj,out object entry) { entry=null; return obj.Managed; } }
}
