{{- define "civicops.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- define "civicops.fullname" -}}
{{- default (printf "%s-%s" .Release.Name (include "civicops.name" .)) .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- define "civicops.selectorLabels" -}}
app.kubernetes.io/name: {{ include "civicops.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
civicops.io/component: app
{{- end -}}
{{- define "civicops.labels" -}}
{{ include "civicops.selectorLabels" . }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | quote }}
{{- end -}}
{{- define "civicops.image" -}}
{{- if .Values.image.digest -}}
{{ printf "%s@%s" .Values.image.repository .Values.image.digest }}
{{- else -}}
{{ printf "%s:%s" .Values.image.repository .Values.image.tag }}
{{- end -}}
{{- end -}}
{{- define "civicops.envFrom" -}}
- configMapRef:
    name: {{ include "civicops.fullname" . }}
{{- if .Values.existingSecret }}
- secretRef:
    name: {{ .Values.existingSecret }}
{{- end }}
{{- end -}}
{{- define "civicops.securityContext" -}}
runAsNonRoot: true
runAsUser: 1654
runAsGroup: 1654
fsGroup: 1654
fsGroupChangePolicy: OnRootMismatch
seccompProfile:
  type: RuntimeDefault
{{- end -}}
{{- define "civicops.containerSecurityContext" -}}
allowPrivilegeEscalation: false
readOnlyRootFilesystem: true
capabilities:
  drop: [ALL]
{{- end -}}
