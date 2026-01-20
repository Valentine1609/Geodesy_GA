using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using TMPro;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

public class GeodeticNetworkGeneratorhand : MonoBehaviour
{
    [Header("Настройки")]
    public float offset = 5f;
    public Transform parentContainer;

    [Header("Raycast")]
    public float raycastHeight = 5f;
    public LayerMask groundLayer;
    public LayerMask obstacleLayer;

    [Header("py файл (указываем через Inspector)")]
    public string pythonFilePath;

    [Header("СКО")]
    public TMP_InputField directionStdevInput;

    [Header("UI")]
    public TMP_Text resultText;
    public TMP_InputField distanceStdevInput;
    public TMP_InputField coordNoiseInput;

    [Header("Геометрическая устойчивость")]
    public bool showWeakLinks = true;
    public float gizmoSphereSize = 0.3f;
    public float condScale = 1000f;
    private List<(Vector3 pos, double cond)> lastSuggestions;

    private class Station
    {
        public Vector3 position;
        public GameObject networkObj;
        public Transform ms60;
        public HashSet<Transform> visibleGrades = new HashSet<Transform>();
    }

    private List<Station> selectedStations = new List<Station>();
    private List<Transform> grades = new List<Transform>();
    private Vector3 groundPos;
    private float alignmentPenalty;

    private void Awake()
    {
        if (parentContainer == null) parentContainer = this.transform;
    }

    public void EvaluateNetworkStability(int topK = 5)
    {
        lastSuggestions = SuggestStationPositions(topK);

        if (lastSuggestions == null || lastSuggestions.Count == 0)
        {
            Debug.Log("Нет предложений по улучшению сети.");
            return;
        }

        foreach (var s in lastSuggestions)
        {
            Debug.Log($"Кандидат: {s.pos}, cond = {s.cond:F2}");

            if (showWeakLinks)
            {
                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.transform.position = s.pos + Vector3.up * 0.2f;
                sphere.transform.localScale = Vector3.one * gizmoSphereSize;

                float t = Mathf.Clamp01((float)((s.cond - 1.0) / condScale));
                sphere.GetComponent<Renderer>().material.color = Color.Lerp(Color.green, Color.red, t);

                Destroy(sphere.GetComponent<Collider>());
                sphere.name = $"CandidateCond_{s.cond:F0}";
            }
        }
    }

    public void OnCheckStabilityButton()
    {
        EvaluateNetworkStability(topK: 5);
    }

    public void OnGenerateButtonPressed()
    {
        GenerateNetwork();
        ExportXML();
        RunPython();
        PrintPointAposterioriFromUnity();
        PrintNetworkReport();
    }

    private void GenerateNetwork()
    {
        // --- Очистка предыдущих линий ---
        foreach (Transform t in parentContainer)
            if (t.name == "Line" || t.name.StartsWith("CandidateCond_") || t.name == "GeodeticLine")
                Destroy(t.gameObject);

        selectedStations.Clear();

        var reMS60 = new Regex(@"MS60\s*\((\d+)\)");
        var reGrade = new Regex(@"Grade\s*\((\d+)\)");

        // --- Сбор пронумерованных MS60 ---
        var msIndexed = new List<(int idx, Transform totalStationTransform)>();
        foreach (var go in GameObject.FindGameObjectsWithTag("total_station"))
        {
            Transform cur = go.transform;
            while (cur != null)
            {
                var m = reMS60.Match(cur.name ?? "");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int idx))
                {
                    msIndexed.Add((idx, go.transform));
                    break;
                }
                cur = cur.parent;
            }
        }
        msIndexed.Sort((a, b) => a.idx.CompareTo(b.idx));

        // --- Сбор пронумерованных Grade ---
        var gradeIndexed = new List<(int idx, Transform gradeTransform)>();
        foreach (var go in GameObject.FindGameObjectsWithTag("grade"))
        {
            var m = reGrade.Match(go.name ?? "");
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups[1].Value, out int idx)) continue;
            gradeIndexed.Add((idx, go.transform));
        }
        gradeIndexed.Sort((a, b) => a.idx.CompareTo(b.idx));

        if (msIndexed.Count == 0 || gradeIndexed.Count == 0)
        {
            Debug.LogWarning("Нет пронумерованных MS60(n) или Grade(n).");
            return;
        }

        // --- Формируем станции ---
        foreach (var pair in msIndexed)
            selectedStations.Add(new Station
            {
                position = pair.totalStationTransform.position,
                networkObj = pair.totalStationTransform.gameObject,
                ms60 = pair.totalStationTransform,
                visibleGrades = new HashSet<Transform>()
            });

        grades = gradeIndexed.Select(x => x.gradeTransform).ToList();

        // --- Линии станция -> марка ---
        foreach (var s in selectedStations)
        {
            Vector3 from = s.ms60.position;
            foreach (var g in grades)
            {
                if (HasLineOfSight(s.ms60, g))
                {
                    CreateLineBetween(s.ms60.position, g.position, true);
                    s.visibleGrades.Add(g);
                }
            }
        }

        // --- Линии между станциями ---
        for (int i = 0; i < selectedStations.Count; i++)
        {
            Vector3 from = selectedStations[i].ms60.position;
            for (int j = i + 1; j < selectedStations.Count; j++)
                if (HasLineOfSight(
                        selectedStations[i].ms60,
                        selectedStations[j].ms60))
                {
                    CreateLineBetween(
                        selectedStations[i].ms60.position,
                        selectedStations[j].ms60.position,
                        true
                    );
                }
        }

        Debug.Log($"GenerateNetwork: построено линий (станция->марка): stations={selectedStations.Count}, grades={grades.Count}");
        PrintNetworkReport();
        ConditioningResult cond = ComputeConditionNumber();

        Debug.Log(
            $"Condition number = {cond.conditionNumber:E3} " +
            $"(λmin={cond.lambdaMin:E3}, λmax={cond.lambdaMax:E3})"
        );
    }


    private bool HasLineOfSight(Transform from, Transform to)
    {
        Vector3 start = from.position + Vector3.up * raycastHeight;
        Vector3 end = to.position + Vector3.up * raycastHeight;

        Vector3 dir = end - start;
        float dist = dir.magnitude;
        if (dist < 0.01f) return true;

        RaycastHit[] hits = Physics.RaycastAll(
            start,
            dir.normalized,
            dist,
            Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Collide
        );

        foreach (var hit in hits)
        {
            Transform h = hit.transform;

            // 1. Игнорируем саму цель
            if (h == to || h.IsChildOf(to))
                continue;

            // 2. Игнорируем станцию-источник
            if (h == from || h.IsChildOf(from))
                continue;

            // 3. Игнорируем все станции
            if (h.CompareTag("total_station") || h.root.CompareTag("total_station"))
                continue;

            // 4. Игнорируем марки
            if (h.CompareTag("grade") || h.root.CompareTag("grade"))
                continue;

            // 5. ВСЁ ОСТАЛЬНОЕ — ПРЕПЯТСТВИЕ
            return false;
        }

        return true;
    }

    // --- Построение линии ---
    private void CreateLineBetween(Vector3 start, Vector3 end, bool visible)
    {
        GameObject lineObj = new GameObject("GeodeticLine");
        lineObj.transform.SetParent(parentContainer, true);

        LineRenderer lr = lineObj.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.SetPositions(new[] { start, end });

        lr.material = new Material(Shader.Find("Sprites/Default"));
        lr.startWidth = lr.endWidth = 0.02f;

        Color color = visible ? Color.green : Color.red;

        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) }
        );

        lr.colorGradient = gradient;
    }

    // Перегрузка: принимает Vector3
    private bool CheckLineOfSightToTarget(Vector3 fromPos, Vector3 toPos)
    {
        // Если у вас есть поле raycastHeight — используем его, иначе 0
        float h = 0f;
        try { h = this.raycastHeight; } catch { h = 0f; }

        Vector3 from = fromPos + Vector3.up * h;
        Vector3 to = toPos + Vector3.up * h;

        return CheckLineOfSightBetweenPoints(from, to);
    }

    // Общая реализация: проверяет все попадания между двумя точками.
    // Любые попадания в объекты без тега "grade" или "total_station" блокируют видимость.
    private bool CheckLineOfSightBetweenPoints(Vector3 from, Vector3 to)
    {
        Vector3 dir = to - from;
        float dist = dir.magnitude;
        if (dist <= 0.0001f) return true;

        RaycastHit[] hits = Physics.RaycastAll(from, dir.normalized, dist);
        foreach (var hit in hits)
        {
            // Пропускаем саму цель и другие станции/марки
            if (hit.transform.CompareTag("grade") || hit.transform.CompareTag("total_station"))
                continue;

            // Если попали в дочерние объекты (иногда коллайдеры на уровне child), можно также игнорировать родителя,
            // если у родителя есть соответствующий тег:
            var root = hit.transform.root;
            if (root != null && (root.CompareTag("grade") || root.CompareTag("total_station")))
                continue;

            // Любой другой объект рассматриваем как препятствие
            return false;
        }

        return true;
    }


    private string ConvertDegreesToDMS(float degrees)
    {
        int d = Mathf.FloorToInt(degrees);
        float remainder = (degrees - d) * 60f;
        int m = Mathf.FloorToInt(remainder);
        float s = (remainder - m) * 60f;
        return $"{d}-{m}-{s.ToString("F2", CultureInfo.InvariantCulture)}";
    }

    public float DistanceStdevMm
    {
        get
        {
            if (distanceStdevInput != null &&
                float.TryParse(distanceStdevInput.text.Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
            {
                return val;
            }
            return 5.0f;
        }
    }

    private float DirectionStdevCc
    {
        get
        {
            if (directionStdevInput != null &&
                float.TryParse(directionStdevInput.text.Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
            {
                return val;
            }
            return 5.0f;
        }
    }

    private void ExportXML()
    {
        if (selectedStations.Count == 0 || grades.Count == 0)
        {
            Debug.LogWarning("Нет данных для экспорта XML");
            return;
        }

        var xml = new StringBuilder();

        xml.AppendLine("<?xml version=\"1.0\" ?>");
        xml.AppendLine("<gama-local xmlns=\"http://www.gnu.org/software/gama/gama-local\">");
        xml.AppendLine("  <network axes-xy=\"ne\" angles=\"right-handed\">");
        xml.AppendLine("    <parameters sigma-apr=\"1.0\" sigma-act=\"apriori\" angles=\"360\"/>");
        xml.AppendLine("    <points-observations>");

        // === Начало координат — первая станция ===
        Vector3 origin = selectedStations[0].ms60.position;
        float coordNoise = CoordNoiseMm;

        // === Экспорт станций ===
        for (int i = 0; i < selectedStations.Count; i++)
        {
            Vector3 rel = selectedStations[i].ms60.position - origin;

            float noiseX = UnityEngine.Random.Range(-coordNoise, coordNoise) / 1000f;
            float noiseZ = UnityEngine.Random.Range(-coordNoise, coordNoise) / 1000f;

            float x = (i == 0) ? 0f : rel.x + noiseX;
            float y = (i == 0) ? 0f : rel.z + noiseZ;

            xml.AppendLine(
                $"      <point id=\"T{i + 1}\" " +
                $"x=\"{x.ToString("F3", CultureInfo.InvariantCulture)}\" " +
                $"y=\"{y.ToString("F3", CultureInfo.InvariantCulture)}\" fix=\"xy\" />"
            );
        }

        // === Экспорт марок ===
        for (int g = 0; g < grades.Count; g++)
        {
            xml.AppendLine($"      <point id=\"G{g + 1}\" adj=\"xy\" />");
        }

        // === Экспорт наблюдений ===
        for (int s = 0; s < selectedStations.Count; s++)
        {
            Station station = selectedStations[s];
            string fromId = $"T{s + 1}";
            Vector3 fromPos = station.ms60.position;

            xml.AppendLine($"      <obs from=\"{fromId}\">");

            // ===== станция → марка =====
            foreach (Transform grade in station.visibleGrades)
            {
                int gIndex = grades.IndexOf(grade);
                if (gIndex < 0) continue;

                string toId = $"G{gIndex + 1}";
                Vector3 toPos = grade.position;

                float az = CalculateAzimuthDegrees(fromPos, toPos);
                string azDms = ConvertDegreesToDMS(az);
                float dist = Vector3.Distance(fromPos, toPos);

                xml.AppendLine(
                    $"        <direction to=\"{toId}\" val=\"{azDms}\" stdev=\"{DirectionStdevCc}\" />"
                );
                xml.AppendLine(
                    $"        <distance to=\"{toId}\" val=\"{dist.ToString("F3", CultureInfo.InvariantCulture)}\" stdev=\"{DistanceStdevMm}\" />"
                );
            }

            // ===== станция ↔ станция =====
            for (int t = 0; t < selectedStations.Count; t++)
            {
                if (t == s) continue;

                Transform toStation = selectedStations[t].ms60;

                // проверяем прямую видимость ТОЛЬКО между станциями
                if (!HasLineOfSight(station.ms60, toStation))
                    continue;

                string toId = $"T{t + 1}";
                Vector3 toPos = toStation.position;

                float az = CalculateAzimuthDegrees(fromPos, toPos);
                string azDms = ConvertDegreesToDMS(az);
                float dist = Vector3.Distance(fromPos, toPos);

                xml.AppendLine(
                    $"        <direction to=\"{toId}\" val=\"{azDms}\" stdev=\"{DirectionStdevCc}\" />"
                );
                xml.AppendLine(
                    $"        <distance to=\"{toId}\" val=\"{dist.ToString("F3", CultureInfo.InvariantCulture)}\" stdev=\"{DistanceStdevMm}\" />"
                );
            }

            xml.AppendLine("      </obs>");
        }

        xml.AppendLine("    </points-observations>");
        xml.AppendLine("  </network>");
        xml.AppendLine("</gama-local>");

        string savePath = Path.Combine(Application.dataPath, "Scenes/GNU/geodetic_network.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(savePath));
        File.WriteAllText(savePath, xml.ToString(), Encoding.UTF8);

        Debug.Log($"XML сохранён: {savePath}");
    }


    private float CoordNoiseMm
    {
        get
        {
            if (coordNoiseInput != null &&
                float.TryParse(coordNoiseInput.text.Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
            {
                return val;
            }
            return 5.0f;
        }
    }

    private float CalculateAzimuthDegrees(Vector3 from, Vector3 to)
    {
        Vector3 dir = to - from;
        float angle = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        return (angle + 360f) % 360f;
    }

    [System.Serializable]
    public class PythonResult
    {
        public string status;
        public string[] errors;
        public Outputs outputs;
    }
    public struct ConditioningResult
    {
        public double conditionNumber;
        public double lambdaMin;
        public double lambdaMax;
    }

    [System.Serializable]
    public class Outputs
    {
        public string stdout;
        public string stderr;
        public string report;
    }

    public void PrintNetworkReport()
    {
        // ===== 1. Геометрическая обусловленность (через AᵀPA) =====
        ConditioningResult res = ComputeConditionNumber();

        if (double.IsInfinity(res.conditionNumber) ||
            double.IsNaN(res.conditionNumber) ||
            res.lambdaMin <= 0.0)
        {
            Debug.LogWarning("Число обусловленности не определено. Сеть вырождена или недостаточно наблюдений.");
            return;
        }

        Debug.Log(
            $"Число обусловленности сети: κ = {res.conditionNumber:F4} " +
            $"(λmin = {res.lambdaMin:E2}, λmax = {res.lambdaMax:E2})"
        );

        // ===== 2. Апостериорные СКО (через SVD матрицы N) =====
        if (!ComputeConditionNumberSVD(out _, out double[,] invN))
        {
            Debug.LogWarning("Не удалось вычислить апостериорные СКО (SVD).");
            return;
        }

        int M = grades.Count;
        if (M == 0) return;

        for (int gi = 0; gi < M; gi++)
        {
            int ix = 2 * gi;
            int iy = ix + 1;

            if (ix >= invN.GetLength(0) || iy >= invN.GetLength(1))
                continue;

            double varX = Math.Abs(invN[ix, ix]);
            double varY = Math.Abs(invN[iy, iy]);

            double sigmaX = Math.Sqrt(varX);
            double sigmaY = Math.Sqrt(varY);
            double sigmaP = Math.Sqrt(sigmaX * sigmaX + sigmaY * sigmaY);

            Debug.Log(
                $"Марка {grades[gi].name}: σX={sigmaX:F4}, σY={sigmaY:F4}, σp={sigmaP:F4}"
            );
        }
    }

    private double[,] CreateZeroMatrix(int n)
    {
        return new double[n, n];
    }

    private double[,] ComputeNormalMatrix()
    {
        if (selectedStations.Count == 0 || grades.Count == 0)
        {
            Debug.LogWarning("Нет станций или марок для вычисления матрицы");
            return null;
        }

        int M = grades.Count;
        int n = 2 * M; // Только X, Y для каждой марки (упрощенно)
        double[,] N = CreateZeroMatrix(n);

        double sigmaDist_m = DistanceStdevMm / 1000.0;
        double wDist = 1.0 / (sigmaDist_m * sigmaDist_m);

        double sigmaAngleRad = DirectionStdevCc / 3600.0 * Math.PI / 180.0;
        double wAngle = 1.0 / (sigmaAngleRad * sigmaAngleRad);

        for (int si = 0; si < selectedStations.Count; si++)
        {
            var s = selectedStations[si];
            if (s.ms60 == null) continue;
            Vector3 sPos = s.ms60.position;

            for (int gi = 0; gi < grades.Count; gi++)
            {
                Transform g = grades[gi];
                if (!HasLineOfSight(s.ms60, g)) continue;


                Vector3 diff = g.position - sPos;
                double r = Math.Sqrt(diff.x * diff.x + diff.z * diff.z);
                if (r <= 1e-6) continue;

                double dx = diff.x / r;
                double dz = diff.z / r;

                int ix = 2 * gi;
                int iy = ix + 1;

                // Вклад измерений расстояния
                N[ix, ix] += wDist * dx * dx;
                N[ix, iy] += wDist * dx * dz;
                N[iy, ix] += wDist * dz * dx;
                N[iy, iy] += wDist * dz * dz;

                // Вклад измерений направлений (упрощенно)
                double denom = dx * dx + dz * dz;
                if (denom > 1e-12)
                {
                    double dbdx = -dz / denom;
                    double dbdz = dx / denom;
                    N[ix, ix] += wAngle * dbdx * dbdx;
                    N[ix, iy] += wAngle * dbdx * dbdz;
                    N[iy, ix] += wAngle * dbdz * dbdx;
                    N[iy, iy] += wAngle * dbdz * dbdz;
                }
            }
        }

        // Регуляризация для устойчивости
        double epsilon = 1e-6;
        for (int i = 0; i < n; i++) N[i, i] += epsilon;

        return N;
    }

    private bool InvertMatrix(double[,] A, out double[,] Ainv)
    {
        int n = A.GetLength(0);
        Ainv = CreateZeroMatrix(n);
        double[,] m = CreateZeroMatrix(n);

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) m[i, j] = A[i, j];
            Ainv[i, i] = 1.0;
        }

        for (int k = 0; k < n; k++)
        {
            int piv = k;
            double maxv = Math.Abs(m[k, k]);
            for (int i = k + 1; i < n; i++)
            {
                double av = Math.Abs(m[i, k]);
                if (av > maxv) { maxv = av; piv = i; }
            }
            if (maxv < 1e-12) return false;

            if (piv != k)
            {
                for (int j = 0; j < n; j++)
                {
                    double tmp = m[k, j]; m[k, j] = m[piv, j]; m[piv, j] = tmp;
                    tmp = Ainv[k, j]; Ainv[k, j] = Ainv[piv, j]; Ainv[piv, j] = tmp;
                }
            }

            double diag = m[k, k];
            for (int j = 0; j < n; j++)
            {
                m[k, j] /= diag;
                Ainv[k, j] /= diag;
            }

            for (int i = 0; i < n; i++)
            {
                if (i == k) continue;
                double factor = m[i, k];
                if (Math.Abs(factor) < 1e-15) continue;
                for (int j = 0; j < n; j++)
                {
                    m[i, j] -= factor * m[k, j];
                    Ainv[i, j] -= factor * Ainv[k, j];
                }
            }
        }

        return true;
    }

    private double MatrixInfinityNorm(double[,] A)
    {
        int n = A.GetLength(0);
        double maxRow = 0.0;
        for (int i = 0; i < n; i++)
        {
            double sum = 0.0;
            for (int j = 0; j < n; j++) sum += Math.Abs(A[i, j]);
            if (sum > maxRow) maxRow = sum;
        }
        return maxRow;
    }

    private ConditioningResult ComputeConditionNumber()
    {
        // 1. Формируем список неизвестных (только марки!)
        int unknownCount = grades.Count * 2; // X,Y для каждой марки
        if (unknownCount == 0)
            return new ConditioningResult { conditionNumber = double.PositiveInfinity };

        // 2. Считаем количество наблюдений
        int obsCount = 0;
        foreach (var s in selectedStations)
            obsCount += s.visibleGrades.Count * 2; // направление + расстояние

        if (obsCount < unknownCount)
            return new ConditioningResult { conditionNumber = double.PositiveInfinity };

        // 3. Матрица A
        double[,] A = new double[obsCount, unknownCount];
        double[,] P = new double[obsCount, obsCount];

        int row = 0;

        for (int s = 0; s < selectedStations.Count; s++)
        {
            Vector3 stPos = selectedStations[s].ms60.position;

            foreach (Transform g in selectedStations[s].visibleGrades)
            {
                int gIndex = grades.IndexOf(g);
                if (gIndex < 0) continue;

                Vector3 grPos = g.position;

                double dx = grPos.x - stPos.x;
                double dy = grPos.z - stPos.z;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < 1e-6) continue;

                int ix = gIndex * 2;
                int iy = ix + 1;

                // ---- направление ----
                A[row, ix] = -dy / (dist * dist);
                A[row, iy] = dx / (dist * dist);
                P[row, row] = 1.0 / (DirectionStdevCc * DirectionStdevCc);
                row++;

                // ---- расстояние ----
                A[row, ix] = dx / dist;
                A[row, iy] = dy / dist;
                P[row, row] = 1.0 / (DistanceStdevMm * DistanceStdevMm);
                row++;
            }
        }

        // 4. N = Aᵀ P A
        double[,] N = MultiplyTranspose(A, P);

        // 5. Собственные значения
        double[] eigen = ComputeEigenvalues(N);

        double lambdaMin = eigen.Where(v => v > 1e-12).Min();
        double lambdaMax = eigen.Max();

        return new ConditioningResult
        {
            lambdaMin = lambdaMin,
            lambdaMax = lambdaMax,
            conditionNumber = lambdaMax / lambdaMin
        };
    }
    private double[,] MultiplyTranspose(double[,] A, double[,] P)
    {
        int m = A.GetLength(0);
        int n = A.GetLength(1);

        double[,] N = new double[n, n];

        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                for (int k = 0; k < m; k++)
                    N[i, j] += A[k, i] * P[k, k] * A[k, j];

        return N;
    }
    private double[] ComputeEigenvalues(double[,] M)
    {
        int n = M.GetLength(0);
        double[] values = new double[n];
        double[,] B = (double[,])M.Clone();

        for (int i = 0; i < n; i++)
        {
            values[i] = PowerIteration(B);
        }
        return values;
    }

    private double PowerIteration(double[,] M)
    {
        int n = M.GetLength(0);
        double[] b = new double[n];
        for (int i = 0; i < n; i++) b[i] = 1.0;

        for (int iter = 0; iter < 50; iter++)
        {
            double[] b1 = new double[n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    b1[i] += M[i, j] * b[j];

            double norm = Math.Sqrt(b1.Sum(v => v * v));
            for (int i = 0; i < n; i++)
                b[i] = b1[i] / norm;
        }

        double lambda = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                lambda += b[i] * M[i, j] * b[j];

        return lambda;
    }


    private bool ComputeConditionNumberSVD(out double condValue, out double[,] invN)
    {
        condValue = double.PositiveInfinity;
        invN = null;

        double[,] Narr = ComputeNormalMatrix();
        if (Narr == null) return false;

        int n = Narr.GetLength(0);
        if (n == 0) return false;

        try
        {
            var Nmat = DenseMatrix.OfArray(Narr);
            var svd = Nmat.Svd(true);
            var S = svd.S;

            if (S.Minimum() < 1e-12) return false;

            condValue = S.Maximum() / S.Minimum();

            var Sinv = DenseMatrix.CreateDiagonal(n, n, i => S[i] > 1e-12 ? 1.0 / S[i] : 0.0);
            var invMat = svd.VT.Transpose() * Sinv * svd.U.Transpose();
            invN = invMat.ToArray();
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"Ошибка при SVD вычислении: {e.Message}");
            return false;
        }
    }

    private List<(Vector3 pos, double cond)> SuggestStationPositions(int topK = 3)
    {
        var suggestions = new List<(Vector3 pos, double cond)>();

        // Упрощенная версия - возвращаем пустой список
        // В реальной реализации здесь можно добавить логику для предложения новых позиций
        Debug.Log("Функция предложения позиций станций не реализована в упрощенной версии");
        return suggestions;
    }

    private void RunPython()
    {
        // Если поле пустое, используем путь по умолчанию
        string pythonFile = string.IsNullOrEmpty(pythonFilePath)
            ? "C:/VR/My projectX/Assets/Scenes/GNU/run_gama.py"
            : pythonFilePath;

        try
        {
            string pyPath = "";

            // 1. Проверяем указанный путь
            if (File.Exists(pythonFile))
            {
                pyPath = pythonFile;
                Debug.Log($"✓ Python файл найден: {pyPath}");
            }
            else
            {
                // 2. Пробуем разные варианты
                string[] possiblePaths =
                {
                pythonFile, // Как указано
                Path.Combine(Application.dataPath, "Scenes/GNU/run_gama.py"),
                Path.Combine(Application.dataPath, "../Scenes/GNU/run_gama.py"),
                "C:/VR/My projectX/Assets/Scenes/GNU/run_gama.py",
                Path.Combine(Directory.GetCurrentDirectory(), "Assets/Scenes/GNU/run_gama.py")
            };

                foreach (string path in possiblePaths)
                {
                    if (File.Exists(path))
                    {
                        pyPath = path;
                        Debug.Log($"✓ Python файл найден: {pyPath}");
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(pyPath))
            {
                if (resultText != null)
                {
                    resultText.text = $"[ERROR] Не найден Python файл!\n";
                    resultText.text += $"Искали: {pythonFile}\n";
                    resultText.text += $"Data Path: {Application.dataPath}\n";
                    resultText.text += $"Текущая папка: {Directory.GetCurrentDirectory()}\n";
                }
                return;
            }

            // Определяем Python
            string pythonCmd = "";
            string[] pythonCommands = { "python", "python3", "py" };

            foreach (string cmd in pythonCommands)
            {
                try
                {
                    var testPsi = new ProcessStartInfo
                    {
                        FileName = cmd,
                        Arguments = "--version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };

                    using (var testProc = Process.Start(testPsi))
                    {
                        if (testProc != null)
                        {
                            testProc.WaitForExit(1000);
                            if (testProc.ExitCode == 0)
                            {
                                pythonCmd = cmd;
                                Debug.Log($"✓ Python найден: {cmd}");
                                break;
                            }
                        }
                    }
                }
                catch { }
            }

            if (string.IsNullOrEmpty(pythonCmd))
            {
                if (resultText != null)
                    resultText.text += "[ERROR] Python не установлен!\n";
                return;
            }

            // Запускаем Python
            var psi = new ProcessStartInfo
            {
                FileName = pythonCmd,
                Arguments = $"\"{pyPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(pyPath)
            };

            if (resultText != null)
                resultText.text = $"[INFO] Запуск: {pythonCmd} \"{pyPath}\"\n";

            using (var proc = Process.Start(psi))
            {
                string output = proc.StandardOutput.ReadToEnd();
                string error = proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                if (!string.IsNullOrEmpty(error) && resultText != null)
                {
                    resultText.text += "[ERROR]\n" + error + "\n";
                }

                if (!string.IsNullOrEmpty(output))
                {
                    try
                    {
                        var result = JsonUtility.FromJson<PythonResult>(output);

                        if (resultText != null)
                        {
                            resultText.text += $"[PYTHON STATUS] {result.status}\n";

                            if (result.errors != null && result.errors.Length > 0)
                                resultText.text += "[ERRORS]\n" + string.Join("\n", result.errors) + "\n";

                            if (result.outputs != null && !string.IsNullOrEmpty(result.outputs.report))
                                resultText.text += "[REPORT]\n" + result.outputs.report + "\n";
                        }
                    }
                    catch
                    {
                        if (resultText != null)
                            resultText.text += output + "\n";
                    }
                }
            }

            // Проверяем выходные файлы
            string outputDir = Path.Combine(Application.dataPath, "StreamingAssets");
            string[] expectedFiles = { "result.svg", "result.txt", "result.xml", "result.html" };

            foreach (var file in expectedFiles)
            {
                string path = Path.Combine(outputDir, file);
                if (File.Exists(path))
                {
                    if (resultText != null)
                        resultText.text += $"[SAVED] {file}\n";
                }
                else
                {
                    if (resultText != null)
                        resultText.text += $"[MISSING] {file}\n";
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Python error: {e}");
            if (resultText != null)
                resultText.text += $"[EXCEPTION] {e.Message}\n";
        }
    }

    public void PrintPointAposterioriFromUnity()
    {
        if (selectedStations.Count == 0 || grades.Count == 0)
        {
            Debug.LogWarning("Станции или марки отсутствуют.");
            return;
        }

        foreach (var g in grades)
        {
            var visibleStations = selectedStations
                .Where(s => s.visibleGrades.Contains(g))
                .ToList();

            if (visibleStations.Count == 0)
            {
                Debug.Log($"Марка {g.name}: недоступна для измерения (не видна ни одной станцией).");
                continue;
            }

            Station nearest = visibleStations.OrderBy(s => Vector3.Distance(s.ms60.position, g.position)).First();
            float dist = Vector3.Distance(nearest.ms60.position, g.position);

            float sx = dist * 0.001f;
            float sy = dist * 0.001f;
            float sp = Mathf.Sqrt(sx * sx + sy * sy);

            Debug.Log($"Марка {g.name}: σX={sx:F4}, σY={sy:F4}, σp={sp:F4}");
        }
    }

    public void ClearStations()
    {
        selectedStations.Clear();

        // Очистка визуализации
        foreach (Transform t in parentContainer)
        {
            if (t.name == "Line" || t.name.StartsWith("CandidateCond_"))
                Destroy(t.gameObject);
        }

        lastSuggestions?.Clear();
        Debug.Log("Станции и визуализация очищены.");
    }
}