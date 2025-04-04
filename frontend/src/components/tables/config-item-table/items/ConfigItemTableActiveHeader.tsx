import { useTranslation } from "react-i18next"

const ConfigItemTableActiveHeader = () => {
  const { t } = useTranslation()
  return <div className="text-center">{t("ConfigList.Header.Active")}</div>
}

export default ConfigItemTableActiveHeader
