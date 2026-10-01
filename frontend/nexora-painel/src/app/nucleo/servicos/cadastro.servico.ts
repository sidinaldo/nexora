import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { EmpresaCriada, NovaEmpresa } from '../modelos';

/** O nome do cabeçalho que leva a chave de administração. Tem de bater, letra por letra, com
 *  `CadastroController.CabecalhoChave` (`src/Nexora.Api/Controllers/CadastroController.cs:44`).
 *
 *  ⚠️ UM ERRO DE DIGITAÇÃO AQUI PRODUZ EXATAMENTE O MESMO 401 DE CHAVE ERRADA. O servidor responde
 *  igual para cabeçalho ausente e para chave inválida, de propósito (`CadastroController.cs:50-56`)
 *  — então um nome errado de cabeçalho é indistinguível de uma chave errada, e o operador passaria
 *  a tarde conferindo a chave, que está certa. É por isso que o teste afirma este literal. */
export const CABECALHO_CHAVE_ADMIN = 'X-Chave-Admin';

/** Criar empresa é a única chamada do painel cuja credencial NÃO é o token de sessão.
 *
 *  Serviço próprio por causa disso, e não um método a mais no `EquipeServico`: lá tudo age sobre
 *  usuários da empresa de quem está logado, e todos os métodos levam Bearer. Um método que recebe
 *  segredo cru no meio de doze que não recebem é armadilha para quem lê depois. */
@Injectable({ providedIn: 'root' })
export class CadastroServico {
  private http = inject(HttpClient);

  /** A chave vai por PARÂMETRO e por REQUISIÇÃO. Este serviço não a guarda em campo nenhum, então
   *  não existe objeto no app que "tem a chave" entre dois envios — e não há o que vazar num dump
   *  de estado, num log de erro ou numa extensão que leia o serviço. */
  criarEmpresa(chave: string, dados: NovaEmpresa): Observable<EmpresaCriada> {
    return this.http.post<EmpresaCriada>(`${API}/cadastro/empresa`, dados, {
      headers: { [CABECALHO_CHAVE_ADMIN]: chave }
    });
  }
}
