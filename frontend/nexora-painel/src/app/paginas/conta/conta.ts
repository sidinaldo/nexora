import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ConfiguracaoServico } from '../../nucleo/servicos/configuracao.servico';
import { EquipeServico } from '../../nucleo/servicos/equipe.servico';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { MinhaConta } from '../../nucleo/modelos';

/** MINHA CONTA: nome, e-mail e senha.
 *
 *  Nenhuma rota daqui recebe id — o alvo é sempre o usuário do contexto. É por isso que a tela
 *  é [Authorize] simples e não por papel: não há como um vendedor editar a conta de outro.
 *
 *  Papel e empresa aparecem em leitura: quem muda papel é o dono, na tela de Equipe. */
@Component({
  selector: 'app-conta',
  imports: [FormsModule],
  templateUrl: './conta.html',
  styleUrl: './conta.css'
})
export class Conta implements OnInit {
  private servico = inject(ConfiguracaoServico);
  private equipe = inject(EquipeServico);
  private auth = inject(AuthServico);
  private toast = inject(ToastServico);

  conta = signal<MinhaConta | null>(null);
  carregando = signal(true);
  erro = signal('');

  // dados
  fNome = signal('');
  fEmail = signal('');
  salvandoDados = signal(false);
  erroDados = signal('');

  // senha
  atual = signal('');
  nova = signal('');
  confirma = signal('');
  salvandoSenha = signal(false);
  erroSenha = signal('');

  get podeTrocarSenha(): boolean {
    return !!this.atual() && this.nova().length >= 8 && this.nova() === this.confirma();
  }

  ngOnInit() { this.carregar(); }

  carregar() {
    this.carregando.set(true);
    this.servico.minhaConta().subscribe({
      next: c => {
        this.conta.set(c);
        this.fNome.set(c.nome);
        this.fEmail.set(c.email);
        this.carregando.set(false);
        this.erro.set('');
      },
      error: () => {
        this.erro.set('Não foi possível carregar a sua conta.');
        this.carregando.set(false);
      }
    });
  }

  salvarDados() {
    this.salvandoDados.set(true);
    this.erroDados.set('');
    this.servico.salvarMinhaConta(this.fNome().trim(), this.fEmail().trim()).subscribe({
      next: () => {
        this.salvandoDados.set(false);
        this.toast.sucesso('Dados atualizados.');
        // O nome aparece na barra lateral e vem do token, que NÃO é reemitido aqui. Atualizar a
        // cópia local evita a tela mostrar o nome antigo até o próximo login.
        this.auth.atualizarNome(this.fNome().trim());
        this.carregar();
      },
      error: e => {
        this.salvandoDados.set(false);
        this.erroDados.set(e.error?.erro ?? 'Não foi possível salvar.');
      }
    });
  }

  trocarSenha() {
    this.erroSenha.set('');
    if (this.nova() !== this.confirma()) {
      this.erroSenha.set('A confirmação não bate com a nova senha.');
      return;
    }
    if (this.nova().length < 8) {
      this.erroSenha.set('A nova senha precisa de ao menos 8 caracteres.');
      return;
    }

    this.salvandoSenha.set(true);
    this.equipe.trocarMinhaSenha(this.atual(), this.nova()).subscribe({
      next: () => {
        this.salvandoSenha.set(false);
        this.atual.set(''); this.nova.set(''); this.confirma.set('');
        this.toast.sucesso('Senha alterada.');
      },
      error: e => {
        // 400 = senha atual incorreta (ou nova muito curta).
        this.erroSenha.set(e.error?.erro ?? 'Não foi possível trocar a senha.');
        this.salvandoSenha.set(false);
      }
    });
  }

  rotuloPapel(p: string): string {
    return p === 'dono' ? 'Dono' : p === 'gestor' ? 'Gestor' : 'Vendedor';
  }
}
