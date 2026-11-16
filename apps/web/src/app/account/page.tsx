import { AccountView } from "@/components/AccountView";
import { ProtectedPage } from "@/components/ProtectedPage";
export default function AccountPage() { return <ProtectedPage><AccountView /></ProtectedPage>; }
