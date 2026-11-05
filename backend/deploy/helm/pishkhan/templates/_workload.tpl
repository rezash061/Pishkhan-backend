{{/*
One Deployment (+ HPA and PDB, and a Service for HTTP hosts). Arguments: root, name (component), image, spec (replicas, maxReplicas,
targetCPU, resources), env (extra variables), http (bool: gets a Service; every host serves /health either way).
*/}}
{{- define "pishkhan.workload" -}}
{{- $root := .root -}}
{{- $fullname := printf "%s-%s" $root.Release.Name .name -}}
apiVersion: apps/v1
kind: Deployment
metadata:
  name: {{ $fullname }}
  labels:
    {{- include "pishkhan.labels" $root | nindent 4 }}
    app.kubernetes.io/component: {{ .name }}
spec:
  replicas: {{ .spec.replicas }}
  revisionHistoryLimit: 5
  strategy:
    type: RollingUpdate
    rollingUpdate: { maxUnavailable: 0, maxSurge: 1 }
  selector:
    matchLabels:
      app.kubernetes.io/instance: {{ $root.Release.Name }}
      app.kubernetes.io/component: {{ .name }}
  template:
    metadata:
      labels:
        {{- include "pishkhan.labels" $root | nindent 8 }}
        app.kubernetes.io/component: {{ .name }}
      annotations:
        checksum/config: {{ toJson $root.Values.config | sha256sum }}
    spec:
      serviceAccountName: {{ include "pishkhan.serviceAccountName" $root }}
      automountServiceAccountToken: false
      {{- with $root.Values.imagePullSecrets }}
      imagePullSecrets:
        {{- toYaml . | nindent 8 }}
      {{- end }}
      securityContext:
        {{- toYaml $root.Values.podSecurityContext | nindent 8 }}
      # Spread replicas over nodes and zones, so losing one does not take a whole host type down.
      topologySpreadConstraints:
        - maxSkew: 1
          topologyKey: kubernetes.io/hostname
          whenUnsatisfiable: ScheduleAnyway
          labelSelector:
            matchLabels:
              app.kubernetes.io/instance: {{ $root.Release.Name }}
              app.kubernetes.io/component: {{ .name }}
        - maxSkew: 1
          topologyKey: topology.kubernetes.io/zone
          whenUnsatisfiable: ScheduleAnyway
          labelSelector:
            matchLabels:
              app.kubernetes.io/instance: {{ $root.Release.Name }}
              app.kubernetes.io/component: {{ .name }}
      # Long enough for in-flight requests and Kafka consumers to finish and commit (MassTransit stops consuming on SIGTERM).
      terminationGracePeriodSeconds: 45
      containers:
        - name: {{ .name }}
          image: {{ include "pishkhan.image" (list $root .image) }}
          imagePullPolicy: {{ $root.Values.image.pullPolicy }}
          ports:
            - name: http
              containerPort: 8080
          envFrom:
            {{- include "pishkhan.envFrom" $root | nindent 12 }}
          env:
            - name: POD_NAME
              valueFrom: { fieldRef: { fieldPath: metadata.name } }
            - name: OTEL_SERVICE_NAME
              value: pishkhan-{{ .name }}
            {{- range $key, $value := .env }}
            - name: {{ $key }}
              value: {{ $value | quote }}
            {{- end }}
          resources:
            {{- toYaml .spec.resources | nindent 12 }}
          securityContext:
            {{- toYaml $root.Values.securityContext | nindent 12 }}
          startupProbe:
            httpGet: { path: {{ $root.Values.probes.startup.path }}, port: http }
            periodSeconds: {{ $root.Values.probes.startup.periodSeconds }}
            failureThreshold: {{ $root.Values.probes.startup.failureThreshold }}
          livenessProbe:
            httpGet: { path: {{ $root.Values.probes.liveness.path }}, port: http }
            periodSeconds: {{ $root.Values.probes.liveness.periodSeconds }}
            failureThreshold: {{ $root.Values.probes.liveness.failureThreshold }}
          readinessProbe:
            httpGet: { path: {{ $root.Values.probes.readiness.path }}, port: http }
            periodSeconds: {{ $root.Values.probes.readiness.periodSeconds }}
            failureThreshold: {{ $root.Values.probes.readiness.failureThreshold }}
          lifecycle:
            # Let the load balancer drop the pod from its endpoints before the process stops accepting requests.
            preStop:
              sleep: { seconds: 5 }
          volumeMounts:
            {{- include "pishkhan.volumeMounts" $root | nindent 12 }}
      volumes:
        {{- include "pishkhan.volumes" $root | nindent 8 }}
---
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: {{ $fullname }}
  labels:
    {{- include "pishkhan.labels" $root | nindent 4 }}
    app.kubernetes.io/component: {{ .name }}
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: {{ $fullname }}
  minReplicas: {{ .spec.replicas }}
  maxReplicas: {{ .spec.maxReplicas }}
  metrics:
    - type: Resource
      resource:
        name: cpu
        target: { type: Utilization, averageUtilization: {{ .spec.targetCPU }} }
  behavior:
    # Scale up fast for a viral post, down slowly so a burst that pauses does not flap.
    scaleUp:
      stabilizationWindowSeconds: 0
      policies: [{ type: Percent, value: 100, periodSeconds: 30 }]
    scaleDown:
      stabilizationWindowSeconds: 300
      policies: [{ type: Pods, value: 1, periodSeconds: 60 }]
---
apiVersion: policy/v1
kind: PodDisruptionBudget
metadata:
  name: {{ $fullname }}
  labels:
    {{- include "pishkhan.labels" $root | nindent 4 }}
    app.kubernetes.io/component: {{ .name }}
spec:
  maxUnavailable: {{ $root.Values.podDisruptionBudget.maxUnavailable }}
  selector:
    matchLabels:
      app.kubernetes.io/instance: {{ $root.Release.Name }}
      app.kubernetes.io/component: {{ .name }}
{{- if .http }}
---
apiVersion: v1
kind: Service
metadata:
  name: {{ $fullname }}
  labels:
    {{- include "pishkhan.labels" $root | nindent 4 }}
    app.kubernetes.io/component: {{ .name }}
spec:
  selector:
    app.kubernetes.io/instance: {{ $root.Release.Name }}
    app.kubernetes.io/component: {{ .name }}
  ports:
    - name: http
      port: 80
      targetPort: http
{{- end }}
{{- end -}}
