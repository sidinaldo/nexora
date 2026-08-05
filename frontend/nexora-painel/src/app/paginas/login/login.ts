import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { OnboardingServico } from '../../nucleo/servicos/onboarding.servico';
import { ThrottleLogin } from '../../nucleo/seguranca/throttle-login';

@Component({
  selector: 'app-login',
  imports: [FormsModule, RouterLink],
  templateUrl: './login.html',
  styleUrl: './login.css'
})
export class Login {
  private auth = inject(AuthServico);
  private onboarding = inject(OnboardingServico);
  private router = inject(Router);

  /** Contagem regressiva quando o rate limit (429) barra. */
  throttle = inject(ThrottleLogin);

  // CAMPOS VAZIOS. O login do Recupera inicializa com e-mail e senha reais de teste, sem
  // condicional de ambiente — o formulário vai para produção pré-preenchido. Não se repete,
  // nem "só em dev": não existe condicional aqui que sobreviva a um build de produção mal
  // configurado.
  email = signal('');
  senha = signal('');

  erro = signal('');
  ocupado = signal(false);

  entrar() {
    this.erro.set('');
    this.ocupado.set(true);

    this.auth.entrar(this.email(), this.senha()).subscribe({
      // Empresa que ainda não terminou os primeiros passos cai no checklist; o resto vai
      // direto atender. É o problema que a tela existe para resolver: o dono entrava numa
      // caixa vazia sem saber por onde começar.
      //
      // Se a consulta falhar, vai para a caixa — onboarding não pode ser motivo de login que
      // não conclui.
      next: () => this.onboarding.carregar().subscribe({
        next: o => this.router.navigate([o.mostrar ? '/comecar' : '/caixa']),
        error: () => this.router.navigate(['/caixa'])
      }),
      error: e => {
        // 429 NÃO vira mensagem fixa: quem mostra é a contagem regressiva reativa, que some
        // sozinha ao zerar. O resto é a resposta genérica (não revela se o e-mail existe).
        if (e.status !== 429) {
          this.erro.set(e.error?.erro ?? 'Não foi possível entrar.');
        }
        this.ocupado.set(false);
      }
    });
  }
}
