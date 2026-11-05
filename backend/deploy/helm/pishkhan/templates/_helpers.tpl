{{- define "pishkhan.name" -}}pishkhan{{- end -}}

{{- define "pishkhan.labels" -}}
app.kubernetes.io/part-of: pishkhan
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/instance: {{ .Release.Name }}
helm.sh/chart: {{ .Chart.Name }}-{{ .Chart.Version | replace "+" "_" }}
{{- end -}}

{{- define "pishkhan.image" -}}
{{- $root := index . 0 -}}{{- $name := index . 1 -}}
{{ $root.Values.image.registry }}/{{ $root.Values.image.repositoryPrefix }}/{{ $name }}:{{ $root.Values.image.tag | default $root.Chart.AppVersion }}
{{- end -}}

{{- define "pishkhan.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}{{ default "pishkhan" .Values.serviceAccount.name }}{{- else -}}{{ default "default" .Values.serviceAccount.name }}{{- end -}}
{{- end -}}

{{/* The env every host shares: the ConfigMap and the existing Secret. */}}
{{- define "pishkhan.envFrom" -}}
- configMapRef:
    name: {{ .Release.Name }}-config
- secretRef:
    name: {{ .Values.existingSecret }}
{{- end -}}

{{/* A pod's writable paths with a read-only root filesystem: /tmp for the runtime and ASP.NET data protection keys live elsewhere. */}}
{{- define "pishkhan.volumes" -}}
- name: tmp
  emptyDir: { sizeLimit: 256Mi }
{{- end -}}
{{- define "pishkhan.volumeMounts" -}}
- name: tmp
  mountPath: /tmp
{{- end -}}
