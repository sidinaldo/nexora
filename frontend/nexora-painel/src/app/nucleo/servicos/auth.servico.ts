import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { Observable, tap } from 'rxjs';
import { API } from '../api-base';
import { LoginResponse, UsuarioAutenticado } from '../modelos';

export const CHAVE_TOKEN = 'nexora.token';
export const CHAVE_USUARIO = 'nexora.usuario';

@Injectable({ providedIn: 'root' })
export class AuthServico {
  private http = inject(HttpClient);
  private router = inject(Router);

  readonly usuario = signal<UsuarioAutenticado | null>(this.usuarioSalvo());
  readonly autenticado = computed(() => this.usuario() !== null);

  /** dono = quem contratou: acesso total, gerencia equipe e conexão. */
  readonly ehDono = computed(() => this.usuario()?.papel === 'dono');
  /** Quem responde pelo NÚMERO. Cancelar uma venda tira faturamento da contagem, e essa é a
   *  linha de corte — a mesma que o `ServicoVendas` aplica no servidor. Aqui é só a tela: a
   *  regra que vale é a do backend, esta só evita oferecer um botão que vai ser recusado. */
  readonly ehGestor = computed(() => this.usuario()?.papel === 'gestor');

  /** dono ou gestor: quem coordena a operação. */
  readonly podeGerenciar = computed(() => {
    const p = this.usuario()?.papel;
    return p === 'dono' || p === 'gestor';
  });

  entrar(email: string, senha: string): Observable<LoginResponse> {
    return this.http.post<LoginResponse>(`${API}/auth/login`, { email, senha }).pipe(
      tap(r => this.aplicarLogin(r))
    );
  }

  /** Usado também pelo aceite de convite, que já devolve token + usuário como o login. */
  aplicarLogin(r: LoginResponse): void {
    localStorage.setItem(CHAVE_TOKEN, r.token);
    localStorage.setItem(CHAVE_USUARIO, JSON.stringify(r.usuario));
    this.usuario.set(r.usuario);
  }

  /** Atualiza só o nome exibido, depois que a pessoa edita a própria conta.
   *
   *  O nome vem do JWT, e o JWT NÃO é reemitido nessa edição — reemitir token a cada troca de
   *  nome trocaria a sessão por um detalhe de cadastro. Sem esta atualização local, a barra
   *  lateral mostraria o nome antigo até o próximo login, e o usuário acharia que não salvou. */
  atualizarNome(nome: string): void {
    const atual = this.usuario();
    if (!atual) return;
    const novo = { ...atual, nome };
    localStorage.setItem(CHAVE_USUARIO, JSON.stringify(novo));
    this.usuario.set(novo);
  }

  sair(): void {
    this.limpar();
    this.router.navigate(['/entrar']);
  }

  /** Chamado pelo interceptor quando a API devolve 401 (token expirado). */
  limpar(): void {
    localStorage.removeItem(CHAVE_TOKEN);
    localStorage.removeItem(CHAVE_USUARIO);
    this.usuario.set(null);
  }

  get token(): string | null {
    return localStorage.getItem(CHAVE_TOKEN);
  }

  private usuarioSalvo(): UsuarioAutenticado | null {
    const bruto = localStorage.getItem(CHAVE_USUARIO);
    if (!bruto) return null;
    try { return JSON.parse(bruto); } catch { return null; }
  }
}
