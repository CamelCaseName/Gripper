using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

public class SkinDeform : MonoBehaviour
{
    private const float jellyBuildup = 0.05f;
    private readonly List<Collider> colliders = new();
    private MeshFilter filter = null;
    private Mesh _mesh;
    private Mesh Mesh
    {
        get
        {
            if (_mesh == null)
            {
                if (renderer is SkinnedMeshRenderer srender)
                {
                    _mesh = srender.sharedMesh;
                }
                else if (renderer.GetType() == typeof(MeshRenderer))
                {
                    if (filter == null)
                    {
                        filter = GetComponent<MeshFilter>();
                    }
                    _mesh = filter.sharedMesh;
                }
            }
            return _mesh;
            throw new InvalidDataException("no MeshRenderer or SkinnedMeshRenderer present on the object");
        }
    }
    bool isSkinned = false;
    private Vector3[] oldVertices;
    private Vector3[] oldNormals;
    //private Vector3[] touchNormals;
    private Vector3[] vertexOffsets;
    private Vector3[] vertexJellyOffsets;
    //todo maybe puit normals, oldnormals and vertices in the same list so we stream all three totally linearly and dont randomly access memory? should give some small caching improvement
    private readonly List<Vector3> vertices = new();
    ExampleVertex[] verticesF;
    private readonly List<Vector3> normals = new();
    private bool[] vertexJellyTouched;
    private float[] touchtimes;
    public Renderer renderer = null;
    public readonly string colliderTag = "SkinColliders";
    public float VelocityThreshhold = 0.03f;
    public float IndentScale = 0.5f;
    public float MaxIndent = 0.08f;
    public float IndentRecoveryTime = 2f;
    public float RecoveryDelayTime = 0.2f;
    public float JellyRadius = 0.1f;
    public float JellyRecoveryTime = 0.7f;
    public float JellyStrength = 1.1f;
    public int ColliderSelect = 0;
    readonly List<Vector3> oldCollPos = new();
    readonly List<int> collType = new();
    readonly Vector3 zero = Vector3.zero;
    bool prevsetAnyVertex = false;
    readonly float[] differences = new float[6];
    //Bounds scaledBounds;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        foreach (var go in GameObject.FindGameObjectsWithTag(colliderTag))
        {
            if (go.TryGetComponent<SphereCollider>(out var coll))
            {
                colliders.Add(coll);
                collType.Add(0);
                oldCollPos.Add(coll.transform.position);
            }
            if (go.TryGetComponent<BoxCollider>(out var box))
            {
                colliders.Add(box);
                collType.Add(1);
                oldCollPos.Add(box.transform.position);
            }
        }
        if (renderer is SkinnedMeshRenderer srender)
        {
            isSkinned = true;
        }
        GetVertices();
        oldVertices = new Vector3[vertices.Count];
        oldNormals = new Vector3[vertices.Count];
        //touchNormals = new Vector3[vertices.Count];
        vertices.CopyTo(oldVertices, 0);
        normals.CopyTo(oldNormals, 0);
        vertexOffsets = new Vector3[vertices.Count];
        vertexJellyOffsets = new Vector3[vertices.Count];
        touchtimes = new float[vertices.Count];
        vertexJellyTouched = new bool[vertices.Count];
    }

    //todo add option to apply a force to the mesh Gameobject after its been smooshed proportional to the total sum of vertex offset
    void Update()
    {
        GetVertices();
        if (vertices.Count == 0)
        {
            return;
        }
        bool setAnyVertex = false;
        bool indent = false;
        float currentTime = Time.time;
        var sindentscale = IndentScale;
        var t = transform;
        Vector3 tlocalScale = t.localScale;
        var sjellyradius = JellyRadius / tlocalScale.magnitude;
        var sjellystrength = JellyStrength;
        var smaxIdent = MaxIndent / tlocalScale.magnitude;
        //scaledBounds = new(renderer.bounds.center, renderer.bounds.size * (1 + jellyFaker));
        for (int c = 0; c < colliders.Count; c++)
        {
            Collider coll = colliders[c];
            if (!coll.bounds.Intersects(renderer.bounds)) //dont need scaled bounds here as the up wave can only start once we press into the obj
            {
                continue;
            }

            Transform collT = coll.transform;
            var vel = (collT.position - oldCollPos[c]).magnitude;
            //move collider to local space
            var transformedColl = t.InverseTransformPoint(collT.position);

            //0 = sphere, 1 = cupe
            int coltype = collType[c];
            if (coltype == 0)
            {
                var tbounds = coll.bounds.extents;
                var checksizeX = (tbounds.x / tlocalScale.x) + sjellyradius;
                var checksizeY = (tbounds.y / tlocalScale.y) + sjellyradius;
                var checksizeZ = (tbounds.z / tlocalScale.z) + sjellyradius;
                var collSize = ((tbounds.x / tlocalScale.x) + (tbounds.y / tlocalScale.y) + (tbounds.z / tlocalScale.z)) / 3;
                var checksize = collSize + sjellyradius;
                for (int i = 0; i < vertices.Count; i++)
                {
                    var vert = vertices[i] - transformedColl;
                    if (vert.x > checksizeX || vert.x < -checksizeX)
                    {
                        continue;
                    }
                    if (vert.y > checksizeY || vert.y < -checksizeY)
                    {
                        continue;
                    }
                    if (vert.z > checksizeZ || vert.z < -checksizeZ)
                    {
                        continue;
                    }
                    if (vertexOffsets[i].sqrMagnitude >= smaxIdent || JellyRadius != 0 && vertexJellyOffsets[i].sqrMagnitude >= smaxIdent)
                    {
                        touchtimes[i] = currentTime;
                        continue;
                    }

                    var diffLength = vert.magnitude;
                    if (diffLength < collSize)
                    {
                        if (isSkinned)
                        {
                            vertexOffsets[i] += Quaternion.FromToRotation(normals[i], oldNormals[i]) * vert * (sindentscale * ((collSize - diffLength) / collSize));
                        }
                        else
                        {
                            vertexOffsets[i] += ((collSize - diffLength) / collSize) * sindentscale * vert;
                        }
                        touchtimes[i] = currentTime;
                        setAnyVertex = true;
                        indent = true;
                    }
                    else if (JellyRadius > 0
                        && !vertexJellyTouched[i]
                        && indent
                        && vertexOffsets[i] == zero
                        && vel > VelocityThreshhold
                        && diffLength > (collSize * collSize)
                        && diffLength < (checksize * checksize))
                    {
                        vertexJellyOffsets[i] = -normals[i] * ((diffLength - (collSize + sjellyradius)) * (sindentscale * sjellystrength * (1 + vel - VelocityThreshhold)));
                        touchtimes[i] = currentTime;
                        vertexJellyTouched[i] = true;
                        setAnyVertex = true;
                    }
                }
            }
            else if (coltype == 1)
            {
                //this seems correct
                var collLocalScale = collT.localScale;

                //angle is finally correct when scaled.
                //todo collider becomes a little smaller the more the mesh is stretched
                Matrix4x4 ltwtranspose = t.localToWorldMatrix.transpose;
                Vector3 collPos = collT.position;
                var gcubeRight = ltwtranspose.MultiplyVector(collT.right);
                gcubeRight.x *= tlocalScale.x;
                gcubeRight.y *= tlocalScale.y;
                gcubeRight.z *= tlocalScale.z;
                gcubeRight = t.rotation * gcubeRight.normalized * (collLocalScale.x / 2);
                var cubeRightP = t.InverseTransformPoint(collPos + gcubeRight);
                var cubeLeftP = t.InverseTransformPoint(collPos - gcubeRight);

                var gcubeUp = ltwtranspose.MultiplyVector(collT.up);
                gcubeUp.x *= tlocalScale.x;
                gcubeUp.y *= tlocalScale.y;
                gcubeUp.z *= tlocalScale.z;
                gcubeUp = t.rotation * gcubeUp.normalized * (collLocalScale.y / 2);
                var cubeUpP = t.InverseTransformPoint(collPos + gcubeUp);
                var cubeDownP = t.InverseTransformPoint(collPos - gcubeUp);

                var gcubeForward = ltwtranspose.MultiplyVector(collT.forward);
                gcubeForward.x *= tlocalScale.x;
                gcubeForward.y *= tlocalScale.y;
                gcubeForward.z *= tlocalScale.z;
                gcubeForward = t.rotation * gcubeForward.normalized * (collLocalScale.z / 2);
                var cubeForwardP = t.InverseTransformPoint(collPos + gcubeForward);
                var cubeBackP = t.InverseTransformPoint(collPos - gcubeForward);

                var cubeForwardN = sindentscale * (cubeForwardP - transformedColl).normalized;
                var cubeBackN = -cubeForwardN;
                var cubeRightN = sindentscale * (cubeRightP - transformedColl).normalized;
                var cubeLeftN = -cubeRightN;
                var cubeUpN = sindentscale * (cubeUpP - transformedColl).normalized;
                var cubeDownN = -cubeUpN;

                //transform the local axes
                var extents = coll.bounds.extents;
                var axisX = t.InverseTransformVector(extents.x, 0, 0);
                var axisY = t.InverseTransformVector(0, extents.y, 0);
                var axisZ = t.InverseTransformVector(0, 0, extents.z);

                //sum their absolute value to get the world extents
                extents.x = Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x);
                extents.y = Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y);
                extents.z = Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z);

                //when the mesh is scaled not uniformly, the collider vector length is not correct

                for (int i = 0; i < vertices.Count; i++)
                {
                    var vert = vertices[i];

                    if (transformedColl.x - extents.x >= vert.x || vert.x >= transformedColl.x + extents.x)
                    {
                        continue;
                    }
                    if (transformedColl.y - extents.y >= vert.y || vert.y >= transformedColl.y + extents.y)
                    {
                        continue;
                    }
                    if (transformedColl.z - extents.z >= vert.z || vert.z >= transformedColl.z + extents.z)
                    {
                        continue;
                    }
                    if (vertexOffsets[i].sqrMagnitude >= smaxIdent || JellyRadius != 0 && vertexJellyOffsets[i].sqrMagnitude >= smaxIdent)
                    {
                        continue;
                    }
                    //calculate closes face of our collider
                    //point has to have 6x  to be inside of our collider
                    var diffDown = Vector3.Dot(cubeDownN, vert - cubeDownP);
                    if (diffDown < 0)
                    {
                        differences[0] = diffDown;
                    }
                    else
                    {
                        continue;
                    }
                    var diffForward = Vector3.Dot(cubeForwardN, vert - cubeForwardP);
                    if (diffForward < 0)
                    {
                        differences[1] = diffForward;
                    }
                    else
                    {
                        continue;
                    }
                    var diffBack = Vector3.Dot(cubeBackN, vert - cubeBackP);
                    if (diffBack < 0)
                    {
                        differences[2] = diffBack;
                    }
                    else
                    {
                        continue;
                    }
                    var diffRight = Vector3.Dot(cubeRightN, vert - cubeRightP);
                    if (diffRight < 0)
                    {
                        differences[3] = diffRight;
                    }
                    else
                    {
                        continue;
                    }
                    var diffLeft = Vector3.Dot(cubeLeftN, vert - cubeLeftP);
                    if (diffLeft < 0)
                    {
                        differences[4] = diffLeft;
                    }
                    else
                    {
                        continue;
                    }
                    var diffUp = Vector3.Dot(cubeUpN, vert - cubeUpP);
                    if (diffUp < 0)
                    {
                        differences[5] = diffUp;
                    }
                    else
                    {
                        continue;
                    }
                    var min = Mathf.Max(differences);
                    if (min >= 0)
                    {
                        continue;
                    }
                    //debugPos.Add(t.TransformPoint(vert));
                    //get the side corrected shove away vector
                    if (min == diffDown)
                    {
                        if (isSkinned)
                        {
                            vertexOffsets[i] -= Quaternion.FromToRotation(normals[i], oldNormals[i]) * (diffDown * cubeDownN);
                        }
                        else
                        {
                            vertexOffsets[i] -= (diffDown * cubeDownN);
                        }
                        //debugDir.Add(t.TransformDirection(cubeDownN));
                        touchtimes[i] = currentTime;
                        setAnyVertex = true;
                        indent = true;
                        continue;
                    }
                    if (min == diffForward)
                    {
                        if (isSkinned)
                        {
                            vertexOffsets[i] -= Quaternion.FromToRotation(normals[i], oldNormals[i]) * (diffForward * cubeForwardN);
                        }
                        else
                        {
                            vertexOffsets[i] -= (diffForward * cubeForwardN);
                        }
                        //debugDir.Add(t.TransformDirection(cubeForwardN));
                        touchtimes[i] = currentTime;
                        setAnyVertex = true;
                        indent = true;
                        continue;
                    }
                    if (min == diffBack)
                    {
                        if (isSkinned)
                        {
                            vertexOffsets[i] -= Quaternion.FromToRotation(normals[i], oldNormals[i]) * (diffBack * cubeBackN);
                        }
                        else
                        {
                            vertexOffsets[i] -= (diffBack * cubeBackN);
                        }
                        //debugDir.Add(t.TransformDirection(cubeBackN));
                        touchtimes[i] = currentTime;
                        setAnyVertex = true;
                        indent = true;
                        continue;
                    }
                    if (min == diffRight)
                    {
                        if (isSkinned)
                        {
                            vertexOffsets[i] -= Quaternion.FromToRotation(normals[i], oldNormals[i]) * (diffRight * cubeRightN);
                        }
                        else
                        {
                            vertexOffsets[i] -= (diffRight * cubeRightN);
                        }
                        //debugDir.Add(t.TransformDirection(cubeRightN));
                        touchtimes[i] = currentTime;
                        setAnyVertex = true;
                        indent = true;
                        continue;
                    }
                    if (min == diffLeft)
                    {
                        if (isSkinned)
                        {
                            vertexOffsets[i] -= Quaternion.FromToRotation(normals[i], oldNormals[i]) * (diffLeft * cubeLeftN);
                        }
                        else
                        {
                            vertexOffsets[i] -= (diffLeft * cubeLeftN);
                        }
                        //debugDir.Add(t.TransformDirection(cubeLeftN));
                        touchtimes[i] = currentTime;
                        setAnyVertex = true;
                        indent = true;
                        continue;
                    }
                    if (min == diffUp)
                    {
                        if (isSkinned)
                        {
                            vertexOffsets[i] -= Quaternion.FromToRotation(normals[i], oldNormals[i]) * (diffUp * cubeUpN);
                        }
                        else
                        {
                            vertexOffsets[i] -= (diffUp * cubeUpN);
                        }
                        touchtimes[i] = currentTime;
                        setAnyVertex = true;
                        indent = true;
                        continue;
                    }
                    //todo also do jelly effect. This probably requires the same setup as the normal indent,
                    //just with a bigger colliding box and only for a point that does not lie inside the real collider
                    //else if (JellyRadius > 0
                    //    && !vertexJellyTouched[i]
                    //    && indent
                    //    && vertexOffsets[i] == zero
                    //    && vel > VelocityThreshhold
                    //    && diffLength > (collSize)
                    //    && diffLength < (checksize))
                    //{
                    //    vertexJellyOffsets[i] = -normals[i] * ((diffLength - (collSize + sjellyradius)) * (sindentscale * sjellystrength * (1 + vel - VelocityThreshhold)));
                    //    touchtimes[i] = currentTime;
                    //    vertexJellyTouched[i] = true;
                    //    setAnyVertex = true;
                    //}
                }
            }
            oldCollPos[c] = coll.transform.position;
        }

        //if we have not set anything this and prev frame why bother
        if (!prevsetAnyVertex && !setAnyVertex)
        {
            return;
        }

        //todo vectorize
        for (int x = 0; x < vertexOffsets.Length; x++)
        {
            var time = (currentTime - (touchtimes[x] + RecoveryDelayTime)) / IndentRecoveryTime;
            var jtime = (currentTime - (touchtimes[x] + jellyBuildup)) / JellyRecoveryTime;

            if (time >= 0 && time < 1)
            {
                vertexOffsets[x] = Lerp(vertexOffsets[x], zero, time);
            }

            //todo add some overswing when going back
            //vertexOffsets[x] = Vector3.Lerp(vertexOffsets[x], Vector3.Lerp(vertexOffsets[x], zero, time), time);
            if (jtime >= 0)
            {
                if (jtime < 1)
                {
                    vertexJellyOffsets[x] = Lerp(vertexJellyOffsets[x], zero, jtime);
                }
                else
                {
                    vertexJellyTouched[x] = false;
                }
            }

            setAnyVertex |= time <= 1f;
        }
        if (setAnyVertex)
        {
            SetVertices();
        }
        else
        {
            //only recalculate after were done manipulating
            if (prevsetAnyVertex)
            {
                Mesh.RecalculateNormals();
                Mesh.RecalculateBounds();
            }
        }
        prevsetAnyVertex = setAnyVertex;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Lerp(Vector3 a, Vector3 b, float t)
    {
        Vector3 result = new()
        {
            x = a.x + (b.x - a.x) * t,
            y = a.y + (b.y - a.y) * t,
            z = a.z + (b.z - a.z) * t
        };
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 OverLerp(Vector3 a, Vector3 b, float times, float t)
    {
        //the second part gives a praubla anchored at (0/1) and (1/1) with maximum wherever you set times + 1.
        var s = t * (1 + times - ((0.5f - t) * (0.5f - t) / (0.25f / times)));
        Vector3 result = new()
        {
            x = a.x + (b.x - a.x) * s,
            y = a.y + (b.y - a.y) * s,
            z = a.z + (b.z - a.z) * s
        };
        return result;
    }

    private void SetVertices()
    {
        float currentTime = Time.time;
        //todo vectorize
        for (int i = 0; i < vertices.Count; i++)
        {
            var jtime = (currentTime - (touchtimes[i])) / jellyBuildup;
            if (jtime >= 0 && jtime < 1)
            {
                vertices[i] = oldVertices[i] + vertexOffsets[i] + Lerp(zero, vertexJellyOffsets[i], jtime);
            }
            else
            {
                vertices[i] = oldVertices[i] + vertexOffsets[i] + vertexJellyOffsets[i];
            }
        }
        Mesh.SetVertices(vertices);
    }

    private void GetVertices()
    {
        if (!isSkinned)
        {
            Mesh.GetVertices(vertices);
            Mesh.GetNormals(normals);
        }
        else
        {
            var buffer = ((SkinnedMeshRenderer)renderer).GetVertexBuffer();
            if (buffer is not null)
            {
                if (vertices.Count == 0)
                {
                    verticesF = new ExampleVertex[buffer.count];
                    buffer.GetData(verticesF);
                    //todo vectorize
                    for (int i = 0; i < verticesF.Length; i++)
                    {
                        vertices.Add(verticesF[i].pos);
                    }

                    for (int i = 0; i < verticesF.Length; i++)
                    {
                        normals.Add(verticesF[i].normal);
                    }

                    oldVertices = new Vector3[vertices.Count];
                    oldNormals = new Vector3[vertices.Count];
                    //touchNormals = new Vector3[vertices.Count];
                    Mesh.vertices.CopyTo(oldVertices, 0);
                    Mesh.normals.CopyTo(oldNormals, 0);
                    vertexOffsets = new Vector3[vertices.Count];
                    vertexJellyOffsets = new Vector3[vertices.Count];
                    touchtimes = new float[vertices.Count];
                    vertexJellyTouched = new bool[vertices.Count];
                }
                else
                {
                    buffer.GetData(verticesF);
                    for (int i = 0; i < verticesF.Length; i++)
                    {
                        vertices[i] = verticesF[i].pos;
                    }

                    for (int i = 0; i < verticesF.Length; i++)
                    {
                        normals[i] = verticesF[i].normal;
                    }
                }
            }
        }
    }
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct ExampleVertex
{
    public Vector3 pos;
    public Vector3 normal;
    public Vector4 tangent;
}
