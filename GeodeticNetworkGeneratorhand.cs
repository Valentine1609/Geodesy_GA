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

        var reMS60 = new Regex(@"(?:MS60|total_station)\s*\((\d+)\)");
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

    // ==========================================================================
    //                           NETWORK REPORT (3D)
    // ==========================================================================

    public void PrintNetworkReport()
    {
        if (grades == null || grades.Count == 0)
        {
            Debug.LogWarning("Нет марок — отчёт сети невозможен.");
            return;
        }

        if (selectedStations.Count < 2)
        {
            Debug.LogWarning($"Недостаточно станций ({selectedStations.Count}). Минимум: 2");
            return;
        }

        // Вычисляем параметры сети
        double cond;
        double[,] covariance;
        bool ok = ComputeConditionNumberAndInverse(out cond, out covariance);

        Debug.Log("=== ГЕОДЕЗИЧЕСКИЙ ОТЧЁТ СЕТИ ===");
        Debug.Log($"Количество марок: {grades.Count}");
        Debug.Log($"Количество станций: {selectedStations.Count}");

        if (ok)
        {
            Debug.Log($"Число обусловленности сети: {cond:N0}");

            // Классификация сети по ГОСТ
            if (cond < 1000)
                Debug.Log($"Класс сети: ВЫСОКОЙ ТОЧНОСТИ (cond < 1000)");
            else if (cond < 5000)
                Debug.Log($"Класс сети: СТАНДАРТНАЯ (cond < 5000)");
            else if (cond < 20000)
                Debug.Log($"Класс сети: ТЕХНИЧЕСКАЯ (cond < 20000)");
            else
                Debug.Log($"Класс сети: НИЗКОЙ ТОЧНОСТИ (cond > 20000)");

            // Анализ ошибок положения марок
            Debug.Log("=== ОШИБКИ ПОЛОЖЕНИЯ МАРОК ===");

            double maxError = 0;
            double avgError = 0;
            int validPoints = 0;

            for (int gi = 0; gi < grades.Count; gi++)
            {
                int idx = gi * 3;
                if (idx + 2 >= covariance.GetLength(0)) continue;

                try
                {
                    double sigmaX = Math.Sqrt(Math.Abs(covariance[idx, idx]));
                    double sigmaY = Math.Sqrt(Math.Abs(covariance[idx + 1, idx + 1]));
                    double sigmaZ = Math.Sqrt(Math.Abs(covariance[idx + 2, idx + 2]));

                    double positionError = Math.Sqrt(sigmaX * sigmaX + sigmaY * sigmaY + sigmaZ * sigmaZ);
                    double horizontalError = Math.Sqrt(sigmaX * sigmaX + sigmaZ * sigmaZ);

                    maxError = Math.Max(maxError, positionError);
                    avgError += positionError;
                    validPoints++;

                    Debug.Log($"Марка {grades[gi].name}: " +
                             $"σX={sigmaX * 1000:F1} мм, " +
                             $"σY={sigmaY * 1000:F1} мм, " +
                             $"σZ={sigmaZ * 1000:F1} мм, " +
                             $"σгор={horizontalError * 1000:F1} мм, " +
                             $"σпол={positionError * 1000:F1} мм");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Ошибка расчета для марки {grades[gi].name}: {ex.Message}");
                }
            }

            if (validPoints > 0)
            {
                avgError /= validPoints;
                Debug.Log($"Средняя ошибка положения: {avgError * 1000:F1} мм");
                Debug.Log($"Максимальная ошибка: {maxError * 1000:F1} мм");

                if (maxError > 0.01)
                    Debug.LogWarning("ВНИМАНИЕ: Максимальная ошибка превышает 10 мм!");
                else if (maxError > 0.005)
                    Debug.Log("Сеть соответствует требованиям средней точности");
                else if (maxError > 0.002)
                    Debug.Log("Сеть соответствует требованиям высокой точности");
                else
                    Debug.Log("Сеть соответствует требованиям прецизионных измерений");
            }
        }
        else
        {
            Debug.LogError("Не удалось вычислить параметры сети. Возможно сеть вырождена.");
        }

        // Анализ геометрии сети
        Debug.Log("=== АНАЛИЗ ГЕОМЕТРИИ СЕТИ ===");

        var coveredGrades = new HashSet<Transform>();
        foreach (var station in selectedStations)
        {
            if (station.visibleGrades != null)
                coveredGrades.UnionWith(station.visibleGrades);
        }

        Debug.Log($"Покрыто марок: {coveredGrades.Count}/{grades.Count} ({coveredGrades.Count / (float)grades.Count * 100:F1}%)");

        var uncoveredGrades = grades.Where(g => !coveredGrades.Contains(g)).ToList();
        if (uncoveredGrades.Count > 0)
        {
            Debug.LogWarning($"Непокрытые марки ({uncoveredGrades.Count}): {string.Join(", ", uncoveredGrades.Select(g => g.name))}");
        }

        int goodAngles = 0;
        int badAngles = 0;

        foreach (var grade in grades)
        {
            var observingStations = selectedStations
                .Where(s => s.visibleGrades != null && s.visibleGrades.Contains(grade))
                .ToList();

            if (observingStations.Count >= 2)
            {
                bool hasGoodAngle = false;
                for (int i = 0; i < observingStations.Count - 1; i++)
                {
                    for (int j = i + 1; j < observingStations.Count; j++)
                    {
                        Vector3 dir1 = observingStations[i].position - grade.position;
                        Vector3 dir2 = observingStations[j].position - grade.position;
                        float angle = Vector3.Angle(dir1, dir2);

                        if (angle >= 30f && angle <= 150f)
                            hasGoodAngle = true;
                    }
                }

                if (hasGoodAngle)
                    goodAngles++;
                else
                    badAngles++;
            }
        }

        Debug.Log($"Марок с хорошей геометрией: {goodAngles}");
        Debug.Log($"Марок с плохой геометрией: {badAngles}");

        if (badAngles > grades.Count * 0.2f)
            Debug.LogWarning("Более 20% марок имеют неудовлетворительную геометрию засечки!");

        Debug.Log("=== ОТЧЁТ ЗАВЕРШЁН ===");
    }

    // ==========================================================================
    //         NORMAL MATRIX + CONDITION NUMBER + PSEUDOINVERSE (3D)
    // ==========================================================================

    private bool ComputeConditionNumberAndInverse(out double condValue, out double[,] invN)
    {
        condValue = double.PositiveInfinity;
        invN = null;

        double quality;
        double[,] N = ComputeNormalMatrix(out quality);
        if (N == null) return false;

        int n = N.GetLength(0);

        if (!JacobiEigenDecomposition(N, out double[] eig, out double[,] eigVec))
        {
            Debug.LogWarning("Eigen decomposition failed.");
            return false;
        }

        double minEig = double.MaxValue;
        double maxEig = double.MinValue;

        for (int i = 0; i < eig.Length; i++)
        {
            if (eig[i] < minEig) minEig = eig[i];
            if (eig[i] > maxEig) maxEig = eig[i];
        }

        if (minEig <= 1e-15)
        {
            Debug.LogWarning("Минимальное собственное значение ~ 0 — сеть вырождена.");
            return false;
        }

        condValue = maxEig / minEig;

        // формируем псевдообратную N⁻¹ = V * Λ⁻¹ * Vᵀ
        invN = new double[n, n];

        for (int k = 0; k < eig.Length; k++)
        {
            double λinv = eig[k] > 1e-15 ? 1.0 / eig[k] : 0.0;

            for (int i = 0; i < n; i++)
            {
                double vik = eigVec[i, k];
                if (Math.Abs(vik) < 1e-15) continue;

                for (int j = 0; j < n; j++)
                {
                    double vjk = eigVec[j, k];
                    if (Math.Abs(vjk) < 1e-15) continue;

                    invN[i, j] += vik * λinv * vjk;
                }
            }
        }

        return true;
    }

    // ==========================================================================
    //                 NORMAL MATRIX BUILDER (3D OBSERVATIONS)
    // ==========================================================================

    private double[,] ComputeNormalMatrix(out double quality)
    {
        quality = double.PositiveInfinity;

        int M = grades.Count;
        if (M == 0) return null;

        int n = M * 3;
        double[,] N = new double[n, n];

        // параметры весов наблюдений
        double sigmaDist = Math.Max(1e-6, DistanceStdevMm / 1000.0);
        double wDist = 1.0 / (sigmaDist * sigmaDist);

        double sigmaAng = (DirectionStdevCc / 3600.0) * Math.PI / 180.0; // ИСПРАВЛЕНО: 3600 сек/градус
        double wAng = 1.0 / (sigmaAng * sigmaAng);

        // --- наполняем N ---
        for (int si = 0; si < selectedStations.Count; si++)
        {
            var st = selectedStations[si];
            if (st == null || st.ms60 == null) continue;

            Vector3 S = st.ms60.position;

            // находим видимые марки
            var visible = new List<(int idx, Transform g)>();

            for (int gi = 0; gi < M; gi++)
            {
                var g = grades[gi];
                if (g == null) continue;

                if (HasLineOfSight(st.ms60, g))
                    visible.Add((gi, g));
            }

            if (visible.Count == 0) continue;

            // расстояния + вертикальные углы
            foreach (var item in visible)
            {
                int gi = item.idx;
                Transform g = item.g;

                Vector3 P = g.position;
                Vector3 d = P - S;

                double dx = d.x;
                double dy = d.y;
                double dz = d.z;

                double r = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (r < 1e-9) continue;

                int bi = gi * 3;

                // ------------ расстояние ------------
                {
                    double[] row = new double[n];
                    row[bi + 0] = dx / r;
                    row[bi + 1] = dy / r;
                    row[bi + 2] = dz / r;
                    AccumulateRowToN(N, row, wDist);
                }

                // ------------ вертикальный угол ------------
                double h = Math.Sqrt(dx * dx + dz * dz);
                if (h > 0.2)
                {
                    double denom = h * (h * h + dy * dy);
                    if (Math.Abs(denom) > 1e-15)
                    {
                        double dvdx = (-dy * dx) / denom;
                        double dvdz = (-dy * dz) / denom;
                        double dvdy = h / (h * h + dy * dy);

                        double[] rowV = new double[n];
                        rowV[bi + 0] = dvdx;
                        rowV[bi + 1] = dvdy;
                        rowV[bi + 2] = dvdz;
                        AccumulateRowToN(N, rowV, wAng);
                    }
                }
            }

            // горизонтальные углы — пары марок
            if (visible.Count >= 2)
            {
                for (int a = 0; a < visible.Count - 1; a++)
                {
                    for (int b = a + 1; b < visible.Count; b++)
                    {
                        int i1 = visible[a].idx;
                        int i2 = visible[b].idx;

                        Vector3 p1 = visible[a].g.position - S;
                        Vector3 p2 = visible[b].g.position - S;

                        double dx1 = p1.x;
                        double dz1 = p1.z;
                        double dx2 = p2.x;
                        double dz2 = p2.z;

                        double d1 = dx1 * dx1 + dz1 * dz1;
                        double d2 = dx2 * dx2 + dz2 * dz2;
                        if (d1 < 1e-12 || d2 < 1e-12) continue;

                        double dth_dx1 = dz1 / d1;
                        double dth_dz1 = -dx1 / d1;
                        double dth_dx2 = -dz2 / d2;
                        double dth_dz2 = dx2 / d2;

                        double[] rowH = new double[n];

                        int bi1 = i1 * 3;
                        int bi2 = i2 * 3;

                        rowH[bi1 + 0] = dth_dx1;
                        rowH[bi1 + 2] = dth_dz1;

                        rowH[bi2 + 0] = dth_dx2;
                        rowH[bi2 + 2] = dth_dz2;

                        AccumulateRowToN(N, rowH, wAng);
                    }
                }
            }
        }

        // === ФИКСАЦИЯ БАЗИСА СЕТИ (ОБЯЗАТЕЛЬНО) ===
        // Марка 0 — фиксируем X, Y, Z
        if (M > 0)
        {
            int b0 = 0 * 3;
            N[b0 + 0, b0 + 0] += 1e6;
            N[b0 + 1, b0 + 1] += 1e6;
            N[b0 + 2, b0 + 2] += 1e6;
        }

        // Марка 1 — фиксируем X и Z (убираем поворот и масштаб)
        if (M > 1)
        {
            int b1 = 1 * 3;
            N[b1 + 0, b1 + 0] += 1e6;
            N[b1 + 2, b1 + 2] += 1e6;
        }

        // общая регуляризация
        for (int i = 0; i < n; i++)
            N[i, i] += 1e-9;

        // оцениваем качество как cond
        if (!JacobiEigenDecomposition(N, out double[] ev, out _))
        {
            quality = double.PositiveInfinity;
            return N;
        }

        double minE = ev.Min();
        double maxE = ev.Max();

        quality = (minE <= 1e-15) ? double.PositiveInfinity : maxE / minE;

        return N;
    }

    // ==========================================================================
    //                JACOBI EIGEN DECOMPOSITION (3D, SYMMETRIC)
    // ==========================================================================

    private bool JacobiEigenDecomposition(double[,] A, out double[] eval, out double[,] evec, int maxIter = 200)
    {
        int n = A.GetLength(0);

        eval = new double[n];
        evec = new double[n, n];

        double[,] D = new double[n, n];

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
                D[i, j] = A[i, j];

            for (int j = 0; j < n; j++)
                evec[i, j] = (i == j ? 1 : 0);
        }

        for (int iter = 0; iter < maxIter; iter++)
        {
            double maxVal = 0;
            int p = 0, q = 1;

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    double v = Math.Abs(D[i, j]);
                    if (v > maxVal)
                    {
                        p = i;
                        q = j;
                        maxVal = v;
                    }
                }
            }

            if (maxVal < 1e-12) break;

            double phi = 0.5 * Math.Atan2(2 * D[p, q], D[q, q] - D[p, p]);
            double c = Math.Cos(phi);
            double s = Math.Sin(phi);

            double Dpp = c * c * D[p, p] - 2 * s * c * D[p, q] + s * s * D[q, q];
            double Dqq = s * s * D[p, p] + 2 * s * c * D[p, q] + c * c * D[q, q];

            D[p, p] = Dpp;
            D[q, q] = Dqq;
            D[p, q] = D[q, p] = 0;

            for (int i = 0; i < n; i++)
            {
                if (i != p && i != q)
                {
                    double dip = c * D[i, p] - s * D[i, q];
                    double diq = s * D[i, p] + c * D[i, q];
                    D[i, p] = D[p, i] = dip;
                    D[i, q] = D[q, i] = diq;
                }

                double vip = c * evec[i, p] - s * evec[i, q];
                double viq = s * evec[i, p] + c * evec[i, q];
                evec[i, p] = vip;
                evec[i, q] = viq;
            }
        }

        for (int i = 0; i < n; i++)
            eval[i] = D[i, i];

        return true;
    }

    // ==========================================================================
    //                         UTILITY: ACCUMULATOR
    // ==========================================================================

    private void AccumulateRowToN(double[,] N, double[] row, double weight)
    {
        int n = N.GetLength(0);

        for (int i = 0; i < n; i++)
        {
            double ri = row[i];
            if (Math.Abs(ri) < 1e-16) continue;

            for (int j = 0; j < n; j++)
            {
                double rj = row[j];
                if (Math.Abs(rj) < 1e-16) continue;

                N[i, j] += weight * ri * rj;
            }
        }
    }

    private bool ComputeConditionNumberSVD(out double condValue, out double[,] invN)
    {
        condValue = double.PositiveInfinity;
        invN = null;

        // Используем 3D-версию матрицы с параметром out double
        double quality;
        double[,] N = ComputeNormalMatrix(out quality);
        if (N == null) return false;

        int n = N.GetLength(0);

        // Якоби-декомпозиция вместо SVD из MathNet
        if (!JacobiEigenDecomposition(N, out double[] eig, out double[,] eigVec))
        {
            Debug.LogWarning("Eigen decomposition failed.");
            return false;
        }

        double minEig = double.MaxValue;
        double maxEig = double.MinValue;

        for (int i = 0; i < eig.Length; i++)
        {
            if (eig[i] < minEig) minEig = eig[i];
            if (eig[i] > maxEig) maxEig = eig[i];
        }

        if (minEig <= 1e-15)
        {
            Debug.LogWarning("Минимальное собственное значение ~ 0 — сеть вырождена.");
            return false;
        }

        condValue = maxEig / minEig;

        // Псевдообратная матрица: N⁻¹ = V * Λ⁻¹ * Vᵀ
        invN = new double[n, n];

        for (int k = 0; k < eig.Length; k++)
        {
            double λinv = eig[k] > 1e-15 ? 1.0 / eig[k] : 0.0;

            for (int i = 0; i < n; i++)
            {
                double vik = eigVec[i, k];
                if (Math.Abs(vik) < 1e-15) continue;

                for (int j = 0; j < n; j++)
                {
                    double vjk = eigVec[j, k];
                    if (Math.Abs(vjk) < 1e-15) continue;

                    invN[i, j] += vik * λinv * vjk;
                }
            }
        }

        return true;
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
