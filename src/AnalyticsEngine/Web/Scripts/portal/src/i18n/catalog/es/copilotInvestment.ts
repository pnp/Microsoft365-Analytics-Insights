import type { copilotInvestment as en } from '../en/copilotInvestment';

/**
 * Spanish (es-ES) text for the Copilot investment report and its administration page.
 *
 * Typed against the English module, so a key added there without a translation here fails the build. The
 * money on this page is always modelled; the Spanish keeps that distinction wherever a figure could be read as
 * an invoice or a measured saving.
 */
const copilotInvestment: Record<keyof typeof en, string> = {
  // Página
  'copilotInvestment.page.title': 'Inversión en Copilot',
  'copilotInvestment.page.intro':
    'Lo que cuesta Copilot, lo que respalda la evidencia de uso y dónde ampliar, reequilibrar o revisar. El dinero de esta página se modela con los supuestos de su organización sobre la actividad observada: no es una factura ni un ahorro medido.',
  'copilotInvestment.page.loading': 'Preparando el informe de inversión\u2026',
  'copilotInvestment.page.range': 'Del {from} al {to} \u00b7 {days} días',
  'copilotInvestment.page.generated': 'Generado el {when}',
  'copilotInvestment.page.filtered': 'Acotado por los filtros de arriba',

  'copilotInvestment.period.ariaLabel': 'Periodo del informe',
  'copilotInvestment.period.last7': 'Últimos 7 días',
  'copilotInvestment.period.last28': 'Últimos 28 días',
  'copilotInvestment.period.last90': 'Últimos 90 días',
  'copilotInvestment.period.last180': 'Últimos 180 días',

  'copilotInvestment.controls.excel': 'Informe de Excel',
  'copilotInvestment.controls.excelReady':
    'Descargue el resumen, el modelo de valor, la vista por organización, las fuentes de créditos, los supuestos y el método en un libro de Excel, con la hoja de personas.',
  'copilotInvestment.controls.excelReadyNoPii':
    'Descargue todas las cifras agregadas, los supuestos y el método en un libro de Excel. Las personas con nombre se omiten: para eso hace falta el permiso See PII.',
  'copilotInvestment.controls.excelLoading': 'Disponible cuando se haya cargado el informe.',
  'copilotInvestment.controls.print': 'Imprima esta pestaña o guárdela como PDF.',
  'copilotInvestment.controls.whatIf': 'Simulación',
  'copilotInvestment.controls.whatIfActive': 'Simulación ({count} cambiados)',
  'copilotInvestment.controls.settings': 'Editar supuestos',

  'copilotInvestment.tab.executive': 'Resumen ejecutivo',
  'copilotInvestment.tab.value': 'Valor y umbral de rentabilidad',
  'copilotInvestment.tab.organisation': 'Organización',
  'copilotInvestment.tab.credits': 'Créditos y fuentes',
  'copilotInvestment.tab.people': 'Personas',
  'copilotInvestment.tab.method': 'Método y fiabilidad',

  'copilotInvestment.availability.userMetadataDisabled': 'La importación de usuarios está desactivada, así que no hay nadie sobre quien informar.',
  'copilotInvestment.availability.copilotSourcesDisabled':
    'No están activadas ni la importación de auditoría de Copilot ni los informes de uso de Copilot de Microsoft, así que no hay evidencia de uso. Los costes y los saldos se siguen mostrando.',
  'copilotInvestment.availability.auditDisabled':
    'La importación de auditoría de Copilot está desactivada. La evidencia procede solo del informe de uso de Microsoft, que no tiene detalle diario para el desglose de una persona.',
  'copilotInvestment.availability.studioDisabled':
    'La importación de créditos de Copilot Studio por usuario está desactivada, así que no se muestran créditos de Studio facturados.',
  'copilotInvestment.availability.creditsDisabled':
    'El historial de créditos de Copilot (versión preliminar) no se está recopilando. Los saldos que aparezcan se conservaron de antes de que se detuviera la recopilación.',

  'copilotInvestment.error.stillBuilding':
    'La evidencia de Copilot en la que se basa este informe está tardando más de lo previsto en prepararse. Vuelva a intentarlo dentro de unos minutos.',
  'copilotInvestment.error.availabilityFailed': 'No se pudo comprobar qué fuentes puede usar este informe (HTTP {status}).',
  'copilotInvestment.error.summaryFailed': 'No se pudo cargar el informe de inversión (HTTP {status}).',
  'copilotInvestment.error.trendFailed': 'No se pudo cargar la evolución de los saldos de créditos (HTTP {status}).',
  'copilotInvestment.error.peopleFailed': 'No se pudo cargar la lista de personas (HTTP {status}).',
  'copilotInvestment.error.personFailed': 'No se pudo cargar la evidencia de esta persona (HTTP {status}).',
  'copilotInvestment.error.personNotFound': 'Esta persona no está en el informe para el periodo y los filtros actuales.',
  'copilotInvestment.error.scenarioInvalid':
    'Las cifras de la simulación incumplen una regla: todos los precios deben ser mayores que cero, un rango necesita los tres valores de menor a mayor y el aprovechamiento es una proporción de 0 a 1.',

  'copilotInvestment.badge.modelled': 'Modelado',
  'copilotInvestment.badge.observed': 'Observado',
  'copilotInvestment.badge.whatIf': 'Simulación',
  'copilotInvestment.badge.tenantWide': 'Todo el inquilino',
  'copilotInvestment.badge.preview': 'Versión preliminar',
  'copilotInvestment.format.multiple': '{value}\u00d7',
  'copilotInvestment.format.perMonth': '{value} al mes',
  'copilotInvestment.format.minutes': '{value} minutos',
  'copilotInvestment.format.range': '{low} \u2013 {high}',
  'copilotInvestment.value.notAvailable': 'No disponible',
  'copilotInvestment.value.unlimited': 'Ilimitado',
  'copilotInvestment.value.unknown': 'Desconocido',
  'copilotInvestment.value.notSet': 'Sin valor',
  'copilotInvestment.value.otherSmallGroups': 'Grupos pequeños combinados',

  // Resumen ejecutivo - cifras principales
  'copilotInvestment.kpi.runRate.label': 'Coste mensual de licencias',
  'copilotInvestment.kpi.runRate.hint': '{holders} licencias a {price} al mes cada una',
  'copilotInvestment.kpi.runRate.what': 'Lo que cuestan cada mes las licencias de Copilot asignadas hoy, al precio de licencia que usted supone.',
  'copilotInvestment.kpi.runRate.how':
    'Todos los titulares de licencia actuales dentro del ámbito, incluidos los que el análisis de adopción no puntuó, multiplicados por el precio de licencia vigente. Un precio de negociación, no una factura.',
  'copilotInvestment.kpi.runRate.formula': 'titulares de licencia \u00d7 precio de licencia al mes',
  'copilotInvestment.kpi.runRate.missing': 'Indique un precio de licencia para modelarlo',

  'copilotInvestment.kpi.active.label': 'Licencias en uso activo',
  'copilotInvestment.kpi.active.hint': '{active} de {holders} titulares analizados usaron Copilot en el periodo',
  'copilotInvestment.kpi.active.what': 'La proporción de titulares analizados con al menos un día de uso observado de Copilot en el periodo.',
  'copilotInvestment.kpi.active.how':
    'Con la misma evidencia que el informe de adopción de Copilot: eventos de auditoría cuando se importan y, si no, el informe de uso de Microsoft. No se cuentan las indicaciones, sino los días con uso.',
  'copilotInvestment.kpi.active.formula': 'titulares con \u2265 1 día activo \u00f7 titulares analizados',

  'copilotInvestment.kpi.costPerActive.label': 'Coste por licencia activa',
  'copilotInvestment.kpi.costPerActive.hint': 'El coste de todas las licencias repartido entre quienes las usan',
  'copilotInvestment.kpi.costPerActive.what': 'Lo que cuesta de hecho cada licencia en uso al mes, una vez pagadas también las que no se usan.',
  'copilotInvestment.kpi.costPerActive.how': 'Se acerca al precio de licencia a medida que las licencias sin uso se recuperan o empiezan a usarse.',
  'copilotInvestment.kpi.costPerActive.formula': 'titulares analizados \u00d7 precio de licencia \u00f7 titulares activos',

  'copilotInvestment.kpi.value.label': 'Valor modelado (central)',
  'copilotInvestment.kpi.value.hint': 'Rango de {low} a {high} al mes',
  'copilotInvestment.kpi.value.what': 'El tiempo que se supone que Copilot devuelve los días en que de verdad se usa, valorado a su coste por hora.',
  'copilotInvestment.kpi.value.how':
    'Se cuenta por día activo, así que más indicaciones el mismo día no suman nada. Solo cuentan los titulares de licencia: las personas sin licencia son una vista aparte, de consumo medido. Un escenario, no un ahorro medido.',
  'copilotInvestment.kpi.value.formula': 'días activos \u00d7 minutos por día activo \u00f7 60 \u00d7 aprovechamiento \u00d7 coste por hora \u00f7 meses del periodo',
  'copilotInvestment.kpi.value.missing': 'Necesita precio de licencia, coste por hora, minutos y aprovechamiento',

  'copilotInvestment.kpi.roi.label': 'Rentabilidad modelada (central)',
  'copilotInvestment.kpi.roi.hint': 'Prudente {low} \u00b7 optimista {high}',
  'copilotInvestment.kpi.roi.what': 'El valor modelado de todos los titulares analizados dividido por el coste modelado de esos mismos titulares.',
  'copilotInvestment.kpi.roi.how':
    'El numerador y el denominador cubren las mismas personas y el mismo periodo. El coste son sus licencias más sus propios créditos de Copilot Studio facturados si hay un precio de Studio; los saldos de créditos en versión preliminar nunca entran.',
  'copilotInvestment.kpi.roi.formula': 'valor modelado \u00f7 (coste de licencias del periodo + créditos de Studio facturados a los titulares \u00d7 precio de Studio)',

  'copilotInvestment.kpi.breakEven.label': 'Umbral de rentabilidad',
  'copilotInvestment.kpi.breakEven.hint': 'Usted supone {assumed} \u00b7 margen de seguridad {margin}',
  'copilotInvestment.kpi.breakEven.what': 'Los minutos que Copilot tendría que devolver cada día activo para que el valor iguale al coste, sin tocar los demás supuestos.',
  'copilotInvestment.kpi.breakEven.how':
    'Un margen de seguridad mayor que cero significa que el supuesto central puede bajar esa proporción antes de que la inversión deje de amortizarse.',
  'copilotInvestment.kpi.breakEven.formula': 'coste modelado \u00f7 (días activos \u00d7 aprovechamiento \u00d7 coste por hora \u00f7 60)',

  'copilotInvestment.kpi.balances.label': 'Saldos de créditos (últimos)',
  'copilotInvestment.kpi.balances.hint': '{consumers} personas con saldo \u00b7 fuera de la rentabilidad',
  'copilotInvestment.kpi.balances.what': 'Los créditos de Copilot que cada persona había consumido en el periodo actual de la fuente, según el barrido más reciente.',
  'copilotInvestment.kpi.balances.how':
    'Un saldo es una lectura puntual. La fuente no indica qué periodo cubre, así que los saldos nunca se suman entre días, nunca se tratan como gasto mensual y nunca entran en la rentabilidad.',
  'copilotInvestment.kpi.balances.source': 'Historial de créditos de Copilot (versión preliminar)',

  'copilotInvestment.kpi.studio.label': 'Créditos de Copilot Studio (facturados)',
  'copilotInvestment.kpi.studio.hint': '{prior} en los {days} días anteriores',
  'copilotInvestment.kpi.studio.hintNoPrior': 'Nada facturado en los {days} días anteriores',
  'copilotInvestment.kpi.studio.what': 'Créditos que Microsoft facturó por agentes de Copilot Studio, por persona y día, en el periodo.',
  'copilotInvestment.kpi.studio.how': 'Un registro diario, así que el periodo anterior de la misma duración es una comparación homogénea.',
  'copilotInvestment.kpi.studio.source': 'Importación de créditos de Copilot Studio por usuario',

  // Resumen ejecutivo - secciones
  'copilotInvestment.exec.valueVsCost.title': 'Valor mensual frente a coste mensual',
  'copilotInvestment.exec.valueVsCost.description':
    'El coste modelado de las licencias analizadas y el rango de valor modelado a su alrededor. Dónde queda el rango respecto al coste resume el caso de inversión en una imagen.',
  'copilotInvestment.exec.valueVsCost.cost': 'Coste modelado',
  'copilotInvestment.exec.valueVsCost.value': 'Valor modelado',
  'copilotInvestment.exec.valueVsCost.summary': 'Coste de {cost} al mes; valor de {low} (prudente) a {high} (optimista), {base} en el escenario central.',
  'copilotInvestment.exec.valueVsCost.missing': 'No se puede modelar el valor hasta que se definan los supuestos.',

  'copilotInvestment.exec.findings.title': 'Lo que dice la evidencia',
  'copilotInvestment.exec.findings.empty': 'Nada destaca en este periodo.',
  'copilotInvestment.exec.actions.title': 'Qué hacer a continuación',
  'copilotInvestment.exec.actions.description':
    'Ordenadas por lo que cambiarían. Cada una nombra a las personas que abarca, para quienes tienen permiso para verlas.',
  'copilotInvestment.exec.actions.empty': 'No se indica ninguna acción para este periodo.',
  'copilotInvestment.exec.actions.seePeople': 'Ver a las {count} personas',
  'copilotInvestment.exec.actions.monthly': '{amount} al mes',
  'copilotInvestment.exec.actions.openTab': 'Abrir',

  'copilotInvestment.exec.distribution.title': 'Con qué frecuencia usan Copilot los titulares',
  'copilotInvestment.exec.distribution.description':
    'Titulares de licencia por días activos al mes. La franja marcada contiene el umbral de rentabilidad: los titulares a su izquierda están por debajo con los supuestos centrales.',
  'copilotInvestment.exec.distribution.breakEven': 'Umbral: {days} días activos al mes',
  'copilotInvestment.exec.distribution.bucket.none': 'Sin uso',
  'copilotInvestment.exec.distribution.bucket.1to2': 'Hasta 2',
  'copilotInvestment.exec.distribution.bucket.2to5': 'De 2 a 5',
  'copilotInvestment.exec.distribution.bucket.5to10': 'De 5 a 10',
  'copilotInvestment.exec.distribution.bucket.10to15': 'De 10 a 15',
  'copilotInvestment.exec.distribution.bucket.15plus': 'Más de 15',
  'copilotInvestment.exec.distribution.holders': '{count} titulares',
  'copilotInvestment.exec.distribution.axis': 'Días activos al mes',

  'copilotInvestment.exec.assumptions.title': 'Supuestos vigentes',
  'copilotInvestment.exec.assumptions.whatIfNote': 'Incluye sus cifras de simulación, que solo ve usted y que no se guardan.',
  'copilotInvestment.exec.assumptions.missingNote': 'Faltan: {fields}.',
  'copilotInvestment.exec.assumptions.unavailable':
    'No se pudieron leer los supuestos guardados, así que no se modela el dinero. La evidencia se sigue mostrando.',

  'copilotInvestment.exec.trust.title': 'Hasta qué punto fiarse',
  'copilotInvestment.exec.trust.high': 'Fiabilidad alta',
  'copilotInvestment.exec.trust.medium': 'Fiabilidad media',
  'copilotInvestment.exec.trust.low': 'Fiabilidad baja',
  'copilotInvestment.exec.trust.none': 'Todas las fuentes están completas para este periodo.',
  'copilotInvestment.exec.trust.more': 'Ver las {count} salvedades',

  // Conclusiones
  'copilotInvestment.finding.noSeats': 'Nadie dentro del ámbito tiene licencia de Copilot, así que no hay inversión en licencias que evaluar.',
  'copilotInvestment.finding.runRate':
    'Las licencias de Copilot cuestan {monthly} al mes para {holders} titulares. Repartido entre quienes las usan, cada licencia activa cuesta {costPerActive}.',
  'copilotInvestment.finding.runRateNoActive': 'Las licencias de Copilot cuestan {monthly} al mes para {holders} titulares, y ninguno usó Copilot en el periodo.',
  'copilotInvestment.finding.valueRange':
    'El uso observado vale de {low} a {high} al mes con sus supuestos, {base} en el escenario central, frente a un coste modelado de {cost}: una rentabilidad de {roiLow} a {roiHigh}.',
  'copilotInvestment.finding.breakEven':
    'Las licencias alcanzan el umbral de rentabilidad si Copilot devuelve {minutes} cada día activo. Usted supone {assumed}, un margen de seguridad del {margin}; el {share} de los titulares está en el umbral o por encima.',
  'copilotInvestment.finding.breakEvenShort':
    'Las licencias necesitan {minutes} cada día activo para alcanzar el umbral, más que los {assumed} que usted supone: en el escenario central aún no se amortizan.',
  'copilotInvestment.finding.assumptionsMissing': 'No se modelan el valor ni la rentabilidad: faltan {count} supuestos.',
  'copilotInvestment.finding.idleSeats': '{idle} titulares de licencia ({share}) no usaron Copilot en absoluto en el periodo.',
  'copilotInvestment.finding.idleSeatsReclaim':
    '{idle} titulares de licencia ({share}) no usaron Copilot en el periodo. Recuperar las {reclaimable} que se pueden recuperar con seguridad ahorra {monthly} al mes y eleva la rentabilidad a {roiAfter}.',
  'copilotInvestment.finding.evidenceConcentration': 'El {topFraction} más activo de los titulares acumula el {topShare} de todos los días activos.',
  'copilotInvestment.finding.creditConcentration':
    'El {topFraction} de los consumidores de créditos con más saldo concentra el {topShare} de los últimos saldos ({consumers} personas con saldo).',
  'copilotInvestment.finding.highSpendThinEvidence':
    '{people} personas tienen los saldos de créditos más altos con poco uso propio de Copilot ({credits} créditos). Más gasto no es más evidencia: revise qué se ejecuta en su nombre.',
  'copilotInvestment.finding.expandEvidenced':
    '{people} personas sin licencia ya usan Copilot más días activos de los que una licencia necesita para amortizarse. Darles licencia costaría {monthly} al mes.',
  'copilotInvestment.finding.expandRecommended': 'El análisis de adopción recomienda licencia para {people} personas que no la tienen.',
  'copilotInvestment.finding.limitBlocked': '{people} personas han agotado su límite de créditos; {evidenced} de ellas muestran un uso intenso y regular.',
  'copilotInvestment.finding.studioTrend':
    'Copilot Studio facturó {credits} créditos en el periodo, frente a {prior} en el periodo anterior de la misma duración ({change}).',
  'copilotInvestment.finding.studioTrendNoPrior':
    'Copilot Studio facturó {credits} créditos en el periodo y nada en el periodo anterior de la misma duración.',
  'copilotInvestment.finding.balancesUnverified':
    '{consumers} personas suman {credits} créditos en los últimos saldos. La fuente no confirma qué periodo cubren, así que se muestran junto a la rentabilidad, no dentro de ella.',
  'copilotInvestment.finding.balancesUnverifiedIfMonthly':
    '{consumers} personas suman {credits} créditos en los últimos saldos, con un periodo sin confirmar. Si fueran el gasto de un mes, la rentabilidad central sería {roiIfMonthly}.',

  // Acciones
  'copilotInvestment.action.reclaimIdle.title': 'Recuperar licencias sin uso',
  'copilotInvestment.action.reclaimIdle.body': '{people} titulares no mostraron uso y se pueden recuperar con seguridad según las reglas de adopción.',
  'copilotInvestment.action.reclaimIdle.after': 'Rentabilidad tras recuperarlas: {roi}.',
  'copilotInvestment.action.enableBelowBreakEven.title': 'Apoyar a quienes están por debajo del umbral',
  'copilotInvestment.action.enableBelowBreakEven.body':
    '{people} titulares usan Copilot, pero aún no lo bastante como para amortizarlo en el escenario central. Un caso típico necesita {days} días activos más al mes.',
  'copilotInvestment.action.enableLightEvidence.title': 'Ayudar a crear el hábito',
  'copilotInvestment.action.enableLightEvidence.body': '{people} titulares han probado Copilot, pero aún no tienen un hábito regular.',
  'copilotInvestment.action.expandEvidenced.title': 'Ampliar licencias a quien ya lo usa',
  'copilotInvestment.action.expandEvidenced.body':
    '{people} personas sin licencia ya usan Copilot al menos {days} días activos al mes, el umbral de rentabilidad de una licencia.',
  'copilotInvestment.action.expandRecommended.title': 'Valorar licencias para las personas recomendadas',
  'copilotInvestment.action.expandRecommended.body': 'El análisis de adopción recomienda licencia para {people} personas que no la tienen.',
  'copilotInvestment.action.reviewHighSpendThinEvidence.title': 'Revisar gasto alto con poca evidencia',
  'copilotInvestment.action.reviewHighSpendThinEvidence.body':
    '{people} personas tienen los saldos más altos ({credits} créditos) con poco uso propio. Busque agentes programados o automatizaciones antes de subir ningún límite.',
  'copilotInvestment.action.raiseLimitEvidenced.title': 'Revisar límites de los usuarios intensivos con evidencia',
  'copilotInvestment.action.raiseLimitEvidenced.body':
    '{people} personas con un uso intenso y regular de Copilot están en su límite de créditos o cerca de él ({credits} créditos).',
  'copilotInvestment.action.licenceMeteredHeavy.title': 'Comparar el consumo medido con una licencia',
  'copilotInvestment.action.licenceMeteredHeavy.body':
    'A {people} personas sin licencia se les facturaron más créditos de Copilot Studio de lo que costaría una licencia.',
  'copilotInvestment.action.improveCoverage.title': 'Cerrar huecos en los datos de créditos',
  'copilotInvestment.action.improveCoverage.body':
    '{people} saldos son parciales, están desactualizados o pertenecen a cuentas que la importación de usuarios no ha emparejado. Revise el recopilador y la importación de usuarios.',
  'copilotInvestment.action.setAssumptions.title': 'Definir los supuestos de inversión',
  'copilotInvestment.action.setAssumptions.body': 'Faltan {count} supuestos, así que no se pueden modelar el valor, la rentabilidad ni el umbral.',
  'copilotInvestment.action.monthlyBalanceCaveat': 'Valorado como si el saldo fuera el gasto de un mes.',

  // Valor y umbral de rentabilidad
  'copilotInvestment.value.scenarios.title': 'Tres escenarios',
  'copilotInvestment.value.scenarios.description':
    'Los mismos días activos observados con los supuestos prudente, central y optimista. Se muestran todas las columnas para poder comprobar las cuentas.',
  'copilotInvestment.value.col.scenario': 'Escenario',
  'copilotInvestment.value.col.minutes': 'Minutos por día activo',
  'copilotInvestment.value.col.realisation': 'Aprovechamiento',
  'copilotInvestment.value.col.assistedHours': 'Horas asistidas',
  'copilotInvestment.value.col.realisedHours': 'Horas aprovechadas',
  'copilotInvestment.value.col.value': 'Valor del periodo',
  'copilotInvestment.value.col.monthly': 'Valor al mes',
  'copilotInvestment.value.col.roi': 'Rentabilidad',
  'copilotInvestment.value.col.net': 'Neto del periodo',
  'copilotInvestment.value.col.atOrAbove': 'Titulares en el umbral o por encima',
  'copilotInvestment.scenario.conservative': 'Prudente',
  'copilotInvestment.scenario.base': 'Central',
  'copilotInvestment.scenario.upside': 'Optimista',
  'copilotInvestment.value.missing.title': 'El valor aún no se modela',
  'copilotInvestment.value.missing.body':
    'Supuestos que faltan: {fields}. Un administrador puede definirlos, o puede probar los suyos con Simulación.',

  'copilotInvestment.value.sensitivity.title': 'Sensibilidad: qué cambia la respuesta',
  'copilotInvestment.value.sensitivity.description':
    'Rentabilidad con cada combinación de los escenarios de minutos y de aprovechamiento. Las celdas por debajo de 1\u00d7 no se amortizan.',
  'copilotInvestment.value.sensitivity.minutes': 'Minutos por día activo',
  'copilotInvestment.value.sensitivity.realisation': 'Aprovechamiento',
  'copilotInvestment.value.sensitivity.cell': '{minutes} minutos, {realisation} de aprovechamiento: rentabilidad {roi}',
  'copilotInvestment.value.sensitivity.paysBack': 'Se amortiza',
  'copilotInvestment.value.sensitivity.short': 'No cubre el coste',
  'copilotInvestment.value.levers.title': 'Otras palancas',
  'copilotInvestment.value.levers.hourlyMinus': 'Coste por hora un 20 % menor',
  'copilotInvestment.value.levers.hourlyPlus': 'Coste por hora un 20 % mayor',
  'copilotInvestment.value.levers.afterReclaim': 'Tras recuperar las licencias sin uso',
  'copilotInvestment.value.levers.ifMonthly': 'Si los saldos de créditos fueran el gasto de un mes',
  'copilotInvestment.value.levers.ifMonthlyNote': 'Solo una prueba de estrés: la fuente no dice qué periodo cubre un saldo.',

  'copilotInvestment.value.breakEven.title': 'Umbral de rentabilidad',
  'copilotInvestment.value.breakEven.minutes': 'Minutos necesarios por día activo',
  'copilotInvestment.value.breakEven.margin': 'Margen de seguridad',
  'copilotInvestment.value.breakEven.daysPerSeat': 'Días activos al mes que necesita una licencia',
  'copilotInvestment.value.breakEven.minutesPerWeekday': 'Minutos por día laborable que necesita una licencia',
  'copilotInvestment.value.breakEven.atOrAbove': 'Titulares en el umbral o por encima',
  'copilotInvestment.value.breakEven.below': 'Titulares por debajo',
  'copilotInvestment.value.breakEven.noEvidence': 'Titulares sin uso',

  'copilotInvestment.value.cohorts.title': 'Por nivel de adopción',
  'copilotInvestment.value.cohorts.description':
    'Titulares agrupados por el nivel de adopción de Copilot. Dónde quedan el coste y el valor en cada nivel muestra qué grupo sostiene la rentabilidad.',
  'copilotInvestment.value.col.band': 'Nivel de adopción',
  'copilotInvestment.value.col.holders': 'Titulares',
  'copilotInvestment.value.col.share': 'Proporción de titulares',
  'copilotInvestment.value.col.medianDays': 'Mediana de días activos',
  'copilotInvestment.value.col.shareOfDays': 'Proporción de días activos',
  'copilotInvestment.value.col.cost': 'Coste modelado',
  'copilotInvestment.value.col.credits': 'Saldos de créditos',
  'copilotInvestment.value.col.studio': 'Créditos de Studio',

  // Organización
  'copilotInvestment.org.title': 'Dónde están la inversión y la evidencia',
  'copilotInvestment.org.description':
    'Titulares agrupados por un atributo del directorio. Los grupos de menos de {min} personas se combinan para que nadie pueda identificarse.',
  'copilotInvestment.org.groupBy': 'Agrupar por',
  'copilotInvestment.org.col.group': 'Grupo',
  'copilotInvestment.org.col.holders': 'Titulares',
  'copilotInvestment.org.col.active': 'Activos',
  'copilotInvestment.org.col.daysPerHolder': 'Días activos por titular al mes',
  'copilotInvestment.org.col.cost': 'Coste modelado',
  'copilotInvestment.org.col.value': 'Valor modelado (central)',
  'copilotInvestment.org.col.roi': 'Rentabilidad',
  'copilotInvestment.org.col.credits': 'Saldos de créditos',
  'copilotInvestment.org.col.studio': 'Créditos de Studio',
  'copilotInvestment.org.col.unlicensedActive': 'Activos sin licencia',
  'copilotInvestment.org.folded': 'Se combinan {count} grupos más pequeños.',
  'copilotInvestment.org.empty': 'Ningún grupo tiene titulares suficientes para mostrarse.',
  'copilotInvestment.org.hidden': 'Este desglose nombra a responsables, para lo que hace falta el permiso See PII.',

  'copilotInvestment.concentration.title': 'Concentración',
  'copilotInvestment.concentration.description':
    'Lo repartidos que están el uso y el gasto. Una curva pegada a la diagonal indica un reparto uniforme; una curva muy combada, que unas pocas personas acumulan la mayor parte.',
  'copilotInvestment.concentration.activeDays': 'Días activos entre los titulares',
  'copilotInvestment.concentration.credits': 'Saldos de créditos entre los consumidores',
  'copilotInvestment.concentration.summary': 'El {fraction} superior acumula el {share}; Gini {gini}.',
  'copilotInvestment.concentration.equality': 'Reparto uniforme',
  'copilotInvestment.concentration.curve': 'Reparto observado',
  'copilotInvestment.concentration.peopleAxis': 'Proporción de personas',
  'copilotInvestment.concentration.amountAxis': 'Proporción del total',
  'copilotInvestment.concentration.none': 'Demasiadas pocas personas para medirlo.',
  'copilotInvestment.quadrants.title': 'Gasto frente a evidencia',
  'copilotInvestment.quadrants.description':
    'Personas con saldo de créditos, según si su gasto y su propio uso de Copilot están por encima de la mediana.',
  'copilotInvestment.quadrants.highSpendHighEvidence': 'Gasto alto, evidencia sólida',
  'copilotInvestment.quadrants.highSpendLowEvidence': 'Gasto alto, poca evidencia',
  'copilotInvestment.quadrants.lowSpendHighEvidence': 'Gasto bajo, evidencia sólida',
  'copilotInvestment.quadrants.lowSpendLowEvidence': 'Gasto bajo, poca evidencia',
  'copilotInvestment.quadrants.people': '{count} personas \u00b7 {credits} créditos',

  // Créditos y fuentes
  'copilotInvestment.sources.title': 'Fuentes',
  'copilotInvestment.sources.adoption': 'Evidencia de Copilot',
  'copilotInvestment.sources.adoption.ok': 'Se analizaron {holders} titulares. Generado el {when}.',
  'copilotInvestment.sources.adoption.off': 'No hay evidencia de Copilot disponible en esta implementación.',
  'copilotInvestment.sources.credits': 'Saldos de créditos de Copilot (versión preliminar)',
  'copilotInvestment.sources.credits.ok':
    'Último barrido el {date}: {observed} personas observadas. Se completaron {sweeps} de los {started} barridos del periodo.',
  'copilotInvestment.sources.credits.none': 'No se han recopilado saldos para este periodo.',
  'copilotInvestment.sources.credits.tablesMissing': 'La base de datos aún no se ha actualizado para el historial de créditos.',
  'copilotInvestment.sources.credits.failed': 'Ahora mismo no se pudo leer el historial de créditos.',
  'copilotInvestment.sources.studio': 'Registro de Copilot Studio',
  'copilotInvestment.sources.studio.ok': 'Última importación el {when}.',
  'copilotInvestment.sources.studio.none': 'No hay créditos de Studio facturados guardados para este periodo.',
  'copilotInvestment.sources.studio.failed': 'La última importación notificó un error.',

  'copilotInvestment.trend.title': 'Saldos de créditos a lo largo del tiempo',
  'copilotInvestment.trend.description':
    'Cada punto son los saldos de un barrido, leídos ese día. Los puntos nunca se suman: un saldo vuelve a bajar cuando la fuente empieza un periodo nuevo. Los días sin barrido no tienen punto.',
  'copilotInvestment.trend.date': 'Fecha del barrido',
  'copilotInvestment.trend.credits': 'Créditos en saldos',
  'copilotInvestment.trend.consumers': 'Personas con saldo',
  'copilotInvestment.trend.point': '{date}: {credits} créditos, {consumers} personas',
  'copilotInvestment.trend.empty': 'No hay historial de saldos en este periodo.',
  'copilotInvestment.trend.loading': 'Cargando el historial de saldos\u2026',

  'copilotInvestment.services.title': 'Por servicio',
  'copilotInvestment.services.description':
    'Cada servicio tal como lo nombra la fuente, con su unidad. Una cantidad en una unidad distinta de los créditos de Copilot se muestra aparte y nunca se suma a los créditos.',
  'copilotInvestment.services.col.service': 'Servicio',
  'copilotInvestment.services.col.unit': 'Unidad',
  'copilotInvestment.services.col.consumers': 'Personas con saldo',
  'copilotInvestment.services.col.quantity': 'Últimos saldos',
  'copilotInvestment.services.col.refused': 'En el límite / sin derecho',
  'copilotInvestment.services.col.unevaluated': 'Sin evaluar',
  'copilotInvestment.services.col.unknown': 'Cantidad desconocida',
  'copilotInvestment.services.unitCredits': 'Créditos de Copilot',
  'copilotInvestment.services.unitOther': 'Otra unidad: {unit}',
  'copilotInvestment.services.unitMissing': 'Sin unidad indicada',
  'copilotInvestment.services.empty': 'Ningún servicio tiene saldo en este periodo.',

  'copilotInvestment.policies.title': 'Directivas de gasto',
  'copilotInvestment.policies.description':
    'Las directivas y los límites que leyó el último barrido. Se aplican a todo el inquilino, sean cuales sean los filtros de arriba.',
  'copilotInvestment.policies.col.name': 'Directiva',
  'copilotInvestment.policies.col.scope': 'Se aplica a',
  'copilotInvestment.policies.col.status': 'Estado',
  'copilotInvestment.policies.col.billing': 'Facturación',
  'copilotInvestment.policies.col.periodLimit': 'Límite del periodo',
  'copilotInvestment.policies.col.perUserLimit': 'Límite por persona',
  'copilotInvestment.policies.col.services': 'Servicios activados',
  'copilotInvestment.policies.scope.allUsers': 'Todos',
  'copilotInvestment.policies.scope.selectedGroups': '{count} grupos',
  'copilotInvestment.policies.scope.selectedGroupsUnknown': 'Grupos seleccionados',
  'copilotInvestment.policies.scope.other': 'Otro',
  'copilotInvestment.policies.status.enabled': 'Activada',
  'copilotInvestment.policies.status.disabled': 'Desactivada',
  'copilotInvestment.policies.status.provisioning': 'En configuración',
  'copilotInvestment.policies.status.provisioningFailed': 'Error de configuración',
  'copilotInvestment.policies.billing.prepaidCredits': 'Créditos prepagados',
  'copilotInvestment.policies.billing.payAsYouGo': 'Pago por uso',
  'copilotInvestment.policies.billing.notSpecified': 'Sin especificar',
  'copilotInvestment.policies.empty': 'Aún no se ha recopilado el contexto de las directivas.',

  'copilotInvestment.tenant.title': 'Saldo de créditos del inquilino',
  'copilotInvestment.tenant.description': 'Lo que todo el inquilino ha comprado y consumido, por método de facturación, según la fuente.',
  'copilotInvestment.tenant.col.method': 'Método de facturación',
  'copilotInvestment.tenant.col.purchased': 'Comprado',
  'copilotInvestment.tenant.col.consumed': 'Consumido',
  'copilotInvestment.tenant.col.remaining': 'Restante',
  'copilotInvestment.tenant.col.updated': 'Actualizado',

  'copilotInvestment.metered.title': 'Personas sin licencia',
  'copilotInvestment.metered.description':
    'Uso y gasto de las personas sin licencia de Copilot. Se mantiene aparte de la rentabilidad de las licencias: su actividad no cuenta como valor de licencia.',
  'copilotInvestment.metered.people': 'Personas en esta vista',
  'copilotInvestment.metered.active': 'Usaron Copilot',
  'copilotInvestment.metered.studio': 'Créditos de Studio facturados',
  'copilotInvestment.metered.balances': 'Saldos de créditos',
  'copilotInvestment.metered.evidenced': 'Lo usan tanto como necesita una licencia',
  'copilotInvestment.metered.recommended': 'Recomendadas para una licencia',
  'copilotInvestment.metered.studioAboveSeat': 'Facturado más que una licencia',

  'copilotInvestment.quality.title': 'Calidad de los datos',
  'copilotInvestment.quality.partial': 'Lecturas parciales',
  'copilotInvestment.quality.stale': 'Anteriores al último barrido',
  'copilotInvestment.quality.unresolved': 'Cuentas no emparejadas con un usuario importado',
  'copilotInvestment.quality.unresolvedCredits': 'Créditos en cuentas no emparejadas',
  'copilotInvestment.quality.period': 'Periodo contable',
  'copilotInvestment.quality.period.unverified': 'Sin confirmar por la fuente',
  'copilotInvestment.quality.period.verified': 'Confirmado por la fuente',
  'copilotInvestment.quality.period.mixed': 'Confirmado solo en algunas lecturas',
  'copilotInvestment.quality.collector': 'Recopilador',
  'copilotInvestment.quality.collector.complete': 'Último barrido completo',
  'copilotInvestment.quality.collector.running': 'Último barrido en curso: faltan {pending} personas',
  'copilotInvestment.quality.collector.other': 'Último barrido en curso',
  'copilotInvestment.quality.filteredNote':
    'Las cuentas no emparejadas solo se cuentan para todo el inquilino, porque no se pueden situar dentro de un filtro.',

  // Personas
  'copilotInvestment.people.title': 'Personas',
  'copilotInvestment.people.description':
    'La licencia, el uso observado y el valor modelado de cada persona. Una señal indica lo que respalda la evidencia, no lo bien que alguien hace su trabajo.',
  'copilotInvestment.people.search': 'Buscar personas',
  'copilotInvestment.people.searchPlaceholder': 'Nombre, departamento o puesto',
  'copilotInvestment.people.seat': 'Licencia',
  'copilotInvestment.people.seat.all': 'Todas las personas',
  'copilotInvestment.people.seat.licensed': 'Titulares de licencia',
  'copilotInvestment.people.seat.unlicensed': 'Sin licencia',
  'copilotInvestment.people.signal': 'Señal',
  'copilotInvestment.people.signal.any': 'Cualquier señal',
  'copilotInvestment.people.flag': 'Indicador',
  'copilotInvestment.people.flag.any': 'Cualquier indicador',
  'copilotInvestment.people.sort': 'Ordenar por',
  'copilotInvestment.people.sort.default': 'Inversión',
  'copilotInvestment.people.sort.activeDays': 'Días activos',
  'copilotInvestment.people.sort.credits': 'Saldo de créditos',
  'copilotInvestment.people.sort.valueToCost': 'Valor sobre coste',
  'copilotInvestment.people.sort.gap': 'Distancia al umbral',
  'copilotInvestment.people.sort.user': 'Nombre',
  'copilotInvestment.people.col.person': 'Persona',
  'copilotInvestment.people.col.seat': 'Licencia',
  'copilotInvestment.people.col.band': 'Nivel',
  'copilotInvestment.people.col.activeDays': 'Días activos al mes',
  'copilotInvestment.people.col.apps': 'Aplicaciones',
  'copilotInvestment.people.col.cost': 'Coste modelado',
  'copilotInvestment.people.col.value': 'Valor modelado',
  'copilotInvestment.people.col.ratio': 'Valor sobre coste',
  'copilotInvestment.people.col.credits': 'Saldo de créditos',
  'copilotInvestment.people.col.studio': 'Créditos de Studio',
  'copilotInvestment.people.col.signal': 'Señal',
  'copilotInvestment.people.seatHeld': 'Con licencia',
  'copilotInvestment.people.noSeat': 'Sin licencia',
  'copilotInvestment.people.count': '{shown} de {total} personas',
  'copilotInvestment.people.previous': 'Anterior',
  'copilotInvestment.people.next': 'Siguiente',
  'copilotInvestment.people.open': 'Abrir la evidencia de {name}',
  'copilotInvestment.people.empty': 'Nadie coincide con estos filtros.',
  'copilotInvestment.people.loading': 'Cargando personas\u2026',

  'copilotInvestment.signal.supported': 'Respaldada',
  'copilotInvestment.signal.plausible': 'Plausible en el optimista',
  'copilotInvestment.signal.belowBreakEven': 'Bajo el umbral',
  'copilotInvestment.signal.noEvidence': 'Sin uso',
  'copilotInvestment.signal.tooNewToJudge': 'Demasiado reciente para valorar',
  'copilotInvestment.signal.strongEvidence': 'Evidencia sólida',
  'copilotInvestment.signal.developing': 'En desarrollo',
  'copilotInvestment.signal.lightEvidence': 'Poca evidencia',
  'copilotInvestment.signal.expandCandidate': 'Candidata a licencia',
  'copilotInvestment.signal.metered': 'Consumo medido',
  'copilotInvestment.signal.observedUnlicensed': 'Observada, sin licencia',

  'copilotInvestment.flag.limitExhausted': 'En el límite de créditos',
  'copilotInvestment.flag.nearLimit': 'Cerca del límite de créditos',
  'copilotInvestment.flag.notEntitled': 'Sin derecho',
  'copilotInvestment.flag.highSpendThinEvidence': 'Gasto alto, poca evidencia',
  'copilotInvestment.flag.creditsPartial': 'Saldo parcial',
  'copilotInvestment.flag.otherUnit': 'Otra unidad',
  'copilotInvestment.flag.reclaimExcluded': 'Excluida de la recuperación',
  'copilotInvestment.flag.accountDisabled': 'Cuenta deshabilitada',
  'copilotInvestment.flag.reclaimCandidate': 'Candidata a recuperar',

  // Desglose de una persona
  'copilotInvestment.person.title': 'Qué respalda la inversión en {name}',
  'copilotInvestment.person.close': 'Cerrar',
  'copilotInvestment.person.loading': 'Cargando la evidencia de esta persona\u2026',
  'copilotInvestment.person.caveat':
    'El valor modelado aplica sus supuestos a los días activos de esta persona. Muestra lo que la evidencia podría respaldar, no lo productivo que es nadie.',
  'copilotInvestment.person.summary.seat': 'Licencia',
  'copilotInvestment.person.summary.activeDays': 'Días activos',
  'copilotInvestment.person.summary.weeks': 'Semanas activas',
  'copilotInvestment.person.summary.weeksValue': '{active} de {total}',
  'copilotInvestment.person.summary.cost': 'Coste modelado',
  'copilotInvestment.person.summary.value': 'Valor modelado',
  'copilotInvestment.person.summary.valueRange': 'De {low} a {high}',
  'copilotInvestment.person.summary.hours': 'Horas asistidas (central)',
  'copilotInvestment.person.evidenceTitle': 'Evidencia',
  'copilotInvestment.person.evidence.noActivity': 'Sin uso de Copilot en el periodo',
  'copilotInvestment.person.evidence.regularHabit': 'Usa Copilot casi todas las semanas',
  'copilotInvestment.person.evidence.occasional': 'Usa Copilot algunas semanas',
  'copilotInvestment.person.evidence.rare': 'Apenas usa Copilot',
  'copilotInvestment.person.evidence.multiApp': 'Lo usa en varias aplicaciones',
  'copilotInvestment.person.evidence.agentUse': 'Trabaja con agentes',
  'copilotInvestment.person.evidence.coworkUse': 'Usa Cowork',
  'copilotInvestment.person.evidence.growing': 'El uso está creciendo',
  'copilotInvestment.person.evidence.declining': 'El uso está bajando',
  'copilotInvestment.person.evidence.promptHistory': 'Historial de indicaciones disponible',
  'copilotInvestment.person.evidence.limitReached': 'Alcanzó un límite de créditos',
  'copilotInvestment.person.evidence.studioAgents': 'Agentes de Copilot Studio facturados',
  'copilotInvestment.person.weeks.title': 'Semana a semana',
  'copilotInvestment.person.weeks.cell': 'Semana del {week}: {days} días activos, {turns} interacciones',
  'copilotInvestment.person.apps.title': 'Dónde se usa Copilot',
  'copilotInvestment.person.apps.row': '{turns} interacciones en {days} días',
  'copilotInvestment.person.apps.none': 'No hay detalle por aplicación para este periodo.',
  'copilotInvestment.person.agents.title': 'Agentes y Cowork',
  'copilotInvestment.person.agents.agentTurns': 'Interacciones con agentes',
  'copilotInvestment.person.agents.coworkTurns': 'Interacciones con Cowork',
  'copilotInvestment.person.agents.totalTurns': 'Todas las interacciones',
  'copilotInvestment.person.prompts.title': 'Indicaciones y respuestas',
  'copilotInvestment.person.prompts.counts': '{prompts} indicaciones, {responses} respuestas',
  'copilotInvestment.person.prompts.notAssessed': 'El historial de interacciones no se importa, así que no se cuentan las indicaciones.',
  'copilotInvestment.person.prompts.categories': 'De qué trataban las indicaciones',
  'copilotInvestment.person.credits.title': 'Saldo de créditos por servicio',
  'copilotInvestment.person.credits.none': 'Esta persona no tiene saldo de créditos.',
  'copilotInvestment.person.credits.remaining': 'Quedan {remaining}',
  'copilotInvestment.person.credits.limit': 'Límite {limit}',
  'copilotInvestment.person.credits.unevaluated': 'Sin evaluar',
  'copilotInvestment.person.credits.history': 'Saldo por barrido',
  'copilotInvestment.person.credits.historyPoint': '{date}: {credits}',
  'copilotInvestment.person.studio.title': 'Agentes de Copilot Studio facturados',
  'copilotInvestment.person.studio.row': '{credits} créditos en {days} días',
  'copilotInvestment.reason.allowed': 'Permitido',
  'copilotInvestment.reason.notEntitled': 'Sin derecho',
  'copilotInvestment.reason.limitExhausted': 'Límite agotado',
  'copilotInvestment.reason.upstreamFailure': 'La fuente no pudo evaluarlo',
  'copilotInvestment.reason.noApplicablePolicy': 'No se aplica ninguna directiva',
  'copilotInvestment.reason.other': 'Otro motivo',

  // Método y fiabilidad
  'copilotInvestment.method.title': 'Cómo se calculan las cifras',
  'copilotInvestment.method.description':
    'Cada cifra modelada es actividad observada multiplicada por un supuesto. Nada de esto es una factura, un precio de contrato ni un ahorro medido, y nada afirma que Copilot haya causado un cambio.',
  'copilotInvestment.method.formulas': 'Fórmulas',
  'copilotInvestment.method.formula.value':
    'Valor modelado = días activos \u00d7 minutos por día activo \u00f7 60 \u00d7 aprovechamiento \u00d7 coste por hora',
  'copilotInvestment.method.formula.cost':
    'Coste modelado = precio de licencia \u00d7 días con licencia \u00f7 30,44 (+ créditos de Studio facturados al titular \u00d7 precio de Studio)',
  'copilotInvestment.method.formula.roi': 'Rentabilidad = valor modelado \u00f7 coste modelado, para los mismos titulares y el mismo periodo',
  'copilotInvestment.method.formula.breakEven':
    'Minutos del umbral = coste modelado \u00f7 (días activos \u00d7 aprovechamiento \u00d7 coste por hora \u00f7 60)',
  'copilotInvestment.method.formula.daysPerSeat':
    'Días activos que necesita una licencia = precio de licencia \u00f7 (minutos \u00f7 60 \u00d7 aprovechamiento \u00d7 coste por hora)',
  'copilotInvestment.method.rules': 'Reglas que respeta el modelo',
  'copilotInvestment.method.rule.activeDays': 'El valor se cuenta por día activo. Enviar más indicaciones el mismo día no suma nada.',
  'copilotInvestment.method.rule.population':
    'La rentabilidad compara a las mismas personas en ambos lados: los titulares que puntuó el análisis de adopción. Las personas sin licencia se presentan por separado.',
  'copilotInvestment.method.rule.balances':
    'Los saldos de créditos en versión preliminar son lecturas puntuales con un periodo sin confirmar. Se muestran junto a la rentabilidad y se someten a pruebas de estrés, pero nunca se suman a ella ni entre días.',
  'copilotInvestment.method.rule.studio':
    'Los créditos facturados de Copilot Studio proceden de un registro diario y solo se comparan con el periodo anterior de la misma duración.',
  'copilotInvestment.method.rule.seats':
    'El coste de licencias usa el historial de licencias de cada titular cuando existe; si no, supone que la licencia actual se tuvo todo el periodo, y lo indica.',
  'copilotInvestment.method.rule.noCausality':
    'Correlación no es causalidad: un uso alto es evidencia de que una licencia se usa, no prueba del valor que genera.',
  'copilotInvestment.method.rule.sources':
    'Los créditos de Studio y los saldos en versión preliminar pueden describir el mismo consumo desde dos fuentes, así que nunca se suman.',
  'copilotInvestment.method.assumptions': 'Supuestos',
  'copilotInvestment.method.col.assumption': 'Supuesto',
  'copilotInvestment.method.col.value': 'Valor',
  'copilotInvestment.method.col.source': 'Origen',
  'copilotInvestment.method.updated': 'Guardado por un administrador el {when} (versión {version}).',
  'copilotInvestment.method.note': 'Nota del administrador',
  'copilotInvestment.method.notDurable':
    'Los supuestos no se pueden guardar en esta implementación: no hay ninguna cadena de conexión de Storage configurada.',
  'copilotInvestment.provenance.tenant': 'Valor de la organización',
  'copilotInvestment.provenance.scenario': 'Su simulación',
  'copilotInvestment.provenance.missing': 'Falta',
  'copilotInvestment.method.trust': 'Salvedades',
  'copilotInvestment.method.trustEmpty': 'No hay salvedades para este periodo.',
  'copilotInvestment.impact.low': 'Reduce la fiabilidad',
  'copilotInvestment.impact.medium': 'Conviene saberlo',
  'copilotInvestment.impact.info': 'Para información',

  'copilotInvestment.field.currency': 'Moneda',
  'copilotInvestment.field.seatPricePerMonth': 'Precio de licencia al mes',
  'copilotInvestment.field.creditPrice': 'Precio de un crédito de Copilot',
  'copilotInvestment.field.studioCreditPrice': 'Precio de un crédito de Copilot Studio',
  'copilotInvestment.field.hourlyCost': 'Coste por hora con cargas',
  'copilotInvestment.field.minutesConservative': 'Minutos por día activo (prudente)',
  'copilotInvestment.field.minutesBase': 'Minutos por día activo (central)',
  'copilotInvestment.field.minutesUpside': 'Minutos por día activo (optimista)',
  'copilotInvestment.field.realisationConservative': 'Aprovechamiento (prudente)',
  'copilotInvestment.field.realisationBase': 'Aprovechamiento (central)',
  'copilotInvestment.field.realisationUpside': 'Aprovechamiento (optimista)',
  'copilotInvestment.field.note': 'Nota',

  'copilotInvestment.trust.adoptionUnavailable': 'No hay evidencia de Copilot, así que no se puede valorar nada.',
  'copilotInvestment.trust.auditMissing': 'Sin la importación de auditoría de Copilot, la evidencia procede solo del informe de uso de Microsoft.',
  'copilotInvestment.trust.usageReportSignal': '{holders} titulares se valoran con el informe de uso de Microsoft, cuyo periodo es el de Microsoft.',
  'copilotInvestment.trust.figuresIncomplete': 'Parte del análisis de adopción falló, así que sus cifras están incompletas.',
  'copilotInvestment.trust.holdersNotAnalysed': 'No se analizaron {holders} titulares (límite de filas). La rentabilidad cubre solo las licencias analizadas.',
  'copilotInvestment.trust.assumptionsMissing': 'Faltan {missing} supuestos, así que el dinero solo se modela en parte.',
  'copilotInvestment.trust.assumptionsUnavailable': 'No se pudieron leer los supuestos guardados.',
  'copilotInvestment.trust.scenarioApplied': 'Se aplican sus propias cifras de simulación. No se guardan y nadie más las ve.',
  'copilotInvestment.trust.seatsCurrentAssignments':
    'El coste de licencias supone que las licencias actuales se tuvieron todo el periodo: falta el historial de licencias de algunos titulares.',
  'copilotInvestment.trust.balancesUnverified':
    'La fuente no confirma qué periodo cubre un saldo de créditos, así que los saldos quedan fuera de la rentabilidad.',
  'copilotInvestment.trust.creditCoverage': 'El último barrido de créditos observó el {coverage} del directorio.',
  'copilotInvestment.trust.creditsStale': 'El saldo de créditos más reciente es {days} días anterior al final del periodo.',
  'copilotInvestment.trust.creditsPartial': 'El {share} de los saldos de créditos son parciales.',
  'copilotInvestment.trust.creditsUnresolved': 'Hay {credits} créditos en {objects} cuentas no emparejadas con un usuario importado.',
  'copilotInvestment.trust.creditsMissing': 'No hay saldos de créditos de Copilot disponibles para este periodo.',
  'copilotInvestment.trust.shortWindow': 'Un periodo de {days} días: las cifras mensuales se extrapolan de una muestra corta.',
  'copilotInvestment.trust.studioImportFailed': 'La última importación de créditos de Copilot Studio notificó un error.',

  'copilotInvestment.band.NeverUsed': 'Nunca lo usó',
  'copilotInvestment.band.Dormant': 'Inactivo',
  'copilotInvestment.band.Trialling': 'Probando',
  'copilotInvestment.band.Developing': 'En desarrollo',
  'copilotInvestment.band.Established': 'Consolidado',
  'copilotInvestment.band.Champion': 'Referente',

  // Panel de simulación
  'copilotInvestment.whatIf.title': 'Simulación',
  'copilotInvestment.whatIf.description':
    'Pruebe sus propias cifras. Se aplican solo a esta página y a su exportación de Excel, no se guardan y nadie más las ve. Deje un campo vacío para usar el valor de la organización.',
  'copilotInvestment.whatIf.apply': 'Aplicar',
  'copilotInvestment.whatIf.reset': 'Volver a los valores de la organización',
  'copilotInvestment.whatIf.close': 'Cerrar',
  'copilotInvestment.whatIf.defaultValue': 'Valor de la organización: {value}',
  'copilotInvestment.whatIf.noDefault': 'Sin valor de la organización',
  'copilotInvestment.whatIf.low': 'Prudente',
  'copilotInvestment.whatIf.base': 'Central',
  'copilotInvestment.whatIf.high': 'Optimista',
  'copilotInvestment.whatIf.minutes': 'Minutos que Copilot devuelve en un día activo',
  'copilotInvestment.whatIf.realisation': 'Proporción de ese tiempo que se convierte en valor (de 0 a 1)',
  'copilotInvestment.whatIf.invalidNumber': 'Escriba un número, con punto o coma para los decimales.',

  // Administración: supuestos
  'copilotInvestment.settings.title': 'Supuestos de inversión en Copilot',
  'copilotInvestment.settings.intro':
    'Las cifras de la organización con las que el informe de inversión en Copilot modela el dinero. No hay nada definido por defecto: el producto no sabe lo que usted paga ni lo que vale una hora del tiempo de su personal, así que cada cifra que necesita un dato que falta lo indica en lugar de suponerlo.',
  'copilotInvestment.settings.loading': 'Cargando los supuestos\u2026',
  'copilotInvestment.settings.notDurable':
    'No hay ninguna cadena de conexión de Storage configurada, así que los supuestos no se pueden guardar. El informe muestra la evidencia sin dinero modelado hasta que la haya.',
  'copilotInvestment.settings.prices': 'Precios',
  'copilotInvestment.settings.prices.description': 'Lo que usted paga. Use las cifras de su contrato; los precios de catálogo pueden no reflejarlas.',
  'copilotInvestment.settings.time': 'Tiempo y valor',
  'copilotInvestment.settings.time.description':
    'Cuánto tiempo devuelve Copilot el día que alguien lo usa y qué parte de ese tiempo se convierte en valor en lugar de holgura. Tres valores para cada uno, de prudente a optimista.',
  'copilotInvestment.settings.currency.hint': 'Código ISO de tres letras, por ejemplo EUR o USD. Todos los precios y el coste por hora están en esta moneda.',
  'copilotInvestment.settings.seatPrice.hint': 'Lo que cuesta al mes una licencia de Microsoft 365 Copilot por persona.',
  'copilotInvestment.settings.creditPrice.hint': 'Lo que cuesta un crédito de Copilot. Sirve para valorar los saldos, que quedan fuera de la rentabilidad.',
  'copilotInvestment.settings.studioCreditPrice.hint': 'Lo que cuesta un crédito facturado de Copilot Studio.',
  'copilotInvestment.settings.hourlyCost.hint': 'Salario más cargas de una hora del tiempo de una persona típica.',
  'copilotInvestment.settings.minutes.hint': 'Hasta 480 minutos. De menor a mayor.',
  'copilotInvestment.settings.realisation.hint': 'Una proporción de 0 a 1. De menor a mayor.',
  'copilotInvestment.settings.note.hint':
    'De dónde salen las cifras: un caso de negocio, una encuesta, un piloto. Se muestra con el informe y en su libro de Excel.',
  'copilotInvestment.settings.save': 'Guardar supuestos',
  'copilotInvestment.settings.saving': 'Guardando\u2026',
  'copilotInvestment.settings.saved': 'Supuestos guardados (versión {version}).',
  'copilotInvestment.settings.unchanged': 'No ha cambiado nada.',
  'copilotInvestment.settings.version': 'Versión {version}, guardada por {who} el {when}.',
  'copilotInvestment.settings.versionNoName': 'Versión {version}, guardada el {when}.',
  'copilotInvestment.settings.neverSaved': 'Nunca se ha guardado.',
  'copilotInvestment.settings.history': 'Historial de cambios',
  'copilotInvestment.settings.history.empty': 'Aún no hay cambios.',
  'copilotInvestment.settings.history.col.version': 'Versión',
  'copilotInvestment.settings.history.col.who': 'Cambiado por',
  'copilotInvestment.settings.history.col.when': 'Cuándo',
  'copilotInvestment.settings.history.col.changes': 'Cambios',
  'copilotInvestment.settings.history.change': '{field}: {from} \u2192 {to}',
  'copilotInvestment.settings.history.empty.value': 'vacío',
  'copilotInvestment.settings.history.unknownWho': 'Desconocido',
  'copilotInvestment.settings.reload': 'Cargar lo más reciente',

  'copilotInvestment.settings.error.invalidRequest': 'No se entendió la solicitud. Vuelva a cargar la página e inténtelo de nuevo.',
  'copilotInvestment.settings.error.versionConflict':
    'Otra persona guardó los supuestos mientras usted los editaba. Vuelva a cargarlos para ver sus cambios y repita los suyos.',
  'copilotInvestment.settings.error.storageNotConfigured':
    'Los supuestos no se pueden guardar: no hay ninguna cadena de conexión de Storage configurada.',
  'copilotInvestment.settings.error.stateUnavailable': 'No se pudo acceder al almacén de supuestos. Inténtelo de nuevo en breve.',
  'copilotInvestment.settings.error.currencyInvalid': 'La moneda debe ser un código ISO de tres letras, como EUR.',
  'copilotInvestment.settings.error.currencyRequired': 'Indique una moneda para los precios y el coste por hora.',
  'copilotInvestment.settings.error.seatPriceOutOfRange': 'El precio de licencia debe ser mayor que 0 y no superar 10.000.',
  'copilotInvestment.settings.error.creditPriceOutOfRange': 'El precio del crédito debe ser mayor que 0 y no superar 100.',
  'copilotInvestment.settings.error.studioCreditPriceOutOfRange': 'El precio del crédito de Studio debe ser mayor que 0 y no superar 100.',
  'copilotInvestment.settings.error.hourlyCostOutOfRange': 'El coste por hora debe ser mayor que 0 y no superar 10.000.',
  'copilotInvestment.settings.error.minutesOutOfRange': 'Los minutos deben estar entre 0 y 480.',
  'copilotInvestment.settings.error.minutesIncomplete': 'Indique los tres valores de minutos, o ninguno.',
  'copilotInvestment.settings.error.minutesOrder': 'Los minutos deben ir de prudente a optimista sin bajar.',
  'copilotInvestment.settings.error.realisationOutOfRange': 'El aprovechamiento debe ser una proporción de 0 a 1.',
  'copilotInvestment.settings.error.realisationIncomplete': 'Indique los tres valores de aprovechamiento, o ninguno.',
  'copilotInvestment.settings.error.realisationOrder': 'El aprovechamiento debe ir de prudente a optimista sin bajar.',
  'copilotInvestment.settings.error.noteTooLong': 'La nota puede tener como máximo 500 caracteres.',
  'copilotInvestment.settings.error.scenarioInvalid': 'Las cifras incumplen una regla.',
  'copilotInvestment.settings.error.loadFailed': 'No se pudieron cargar los supuestos.',
  'copilotInvestment.settings.error.saveFailed': 'No se pudieron guardar los supuestos.',
};

export default copilotInvestment;
